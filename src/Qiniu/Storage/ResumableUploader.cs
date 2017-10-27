﻿using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Threading;
using Qiniu.Util;
using Qiniu.Http;
using Newtonsoft.Json;

namespace Qiniu.Storage
{
    /// <summary>
    /// 分片上传/断点续上传，适合于以下"情形2~3":  
    /// (1)网络较好并且待上传的文件体积较小时(比如100MB或更小一点)使用简单上传;
    /// (2)文件较大或者网络状况不理想时请使用分片上传;
    /// (3)文件较大并且需要支持断点续上传，请使用分片上传(断点续上传)
    /// 上传时需要提供正确的上传凭证(由对应的上传策略生成)
    /// 上传策略 https://developer.qiniu.com/kodo/manual/1206/put-policy
    /// 上传凭证 https://developer.qiniu.com/kodo/manual/1208/upload-token
    /// </summary>
    public class ResumableUploader
    {
        private Config config;
        //分片上传块的大小，固定为4M，不可修改
        private const int BLOCK_SIZE = 4 * 1024 * 1024;

        // HTTP请求管理器(GET/POST等)
        private HttpManager httpManager;

        /// <summary>
        /// 初始化
        /// </summary>
        /// <param name="config">分片上传的配置信息</param>
        public ResumableUploader(Config config)
        {
            if (config == null)
            {
                this.config = new Config();
            }
            else
            {
                this.config = config;
            }
            this.httpManager = new HttpManager();
        }


        /// <summary>
        /// 分片上传，支持断点续上传，带有自定义进度处理、高级控制功能
        /// </summary>
        /// <param name="localFile">本地待上传的文件名</param>
        /// <param name="key">要保存的文件名称</param>
        /// <param name="token">上传凭证</param>
        /// <param name="putExtra">上传可选配置</param>
        /// <returns>上传文件后的返回结果</returns>
        public HttpResult UploadFile(string localFile, string key, string token, PutExtra putExtra)
        {
            try
            {
                FileStream fs = new FileStream(localFile, FileMode.Open);
                return this.UploadStream(fs, key, token, putExtra);
            }
            catch (Exception ex)
            {
                HttpResult ret = HttpResult.InvalidFile;
                ret.RefText = ex.Message;
                return ret;
            }
        }



        /// <summary>
        /// 分片上传/断点续上传，带有自定义进度处理和上传控制，检查CRC32，可自动重试
        /// </summary>
        /// <param name="stream">待上传文件流</param>
        /// <param name="key">要保存的文件名称</param>
        /// <param name="upToken">上传凭证</param>
        /// <param name="putExtra">可选配置参数</param>
        /// <returns>上传文件后返回结果</returns>
        public HttpResult UploadStream(Stream stream, string key, string upToken, PutExtra putExtra)
        {
            HttpResult result = new HttpResult();

            //check put extra
            if (putExtra == null)
            {
                putExtra = new PutExtra();
            }
            if (putExtra.ProgressHandler == null)
            {
                putExtra.ProgressHandler = DefaultUploadProgressHandler;
            }
            if (putExtra.UploadController == null)
            {
                putExtra.UploadController = DefaultUploadController;
            }

            if (!(putExtra.BlockUploadThreads > 0 && putExtra.BlockUploadThreads <= 64))
            {
                putExtra.BlockUploadThreads = 1;
            }

            using (stream)
            {
                //start to upload
                try
                {
                    long uploadedBytes = 0;
                    long fileSize = stream.Length;
                    int blockCount = (int)((fileSize + BLOCK_SIZE - 1) / BLOCK_SIZE);

                    //check resume record file
                    ResumeInfo resumeInfo = null;
                    if (File.Exists(putExtra.ResumeRecordFile))
                    {
                        resumeInfo = ResumeHelper.Load(putExtra.ResumeRecordFile);
                        if (resumeInfo != null && fileSize == resumeInfo.FileSize)
                        {
                            //check whether ctx expired
                            if (UnixTimestamp.IsContextExpired(resumeInfo.ExpiredAt))
                            {
                                resumeInfo = null;
                            }
                        }
                    }
                    if (resumeInfo == null)
                    {
                        resumeInfo = new ResumeInfo()
                        {
                            FileSize = fileSize,
                            BlockCount = blockCount,
                            Contexts = new string[blockCount],
                            ExpiredAt = 0,
                        };
                    }

                    //calc upload progress
                    for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
                    {
                        string context = resumeInfo.Contexts[blockIndex];
                        if (!string.IsNullOrEmpty(context))
                        {
                            uploadedBytes += BLOCK_SIZE;
                        }
                    }

                    //set upload progress
                    putExtra.ProgressHandler(uploadedBytes, fileSize);

                    //init block upload error
                    //check not finished blocks to upload
                    UploadControllerAction upCtrl = putExtra.UploadController();
                    ManualResetEvent manualResetEvent = new ManualResetEvent(false);
                    Dictionary<int, byte[]> blockDataDict = new Dictionary<int, byte[]>();
                    Dictionary<int, HttpResult> blockMakeResults = new Dictionary<int, HttpResult>();
                    Dictionary<string, long> uploadedBytesDict = new Dictionary<string, long>();
                    uploadedBytesDict.Add("UploadProgress", uploadedBytes);
                    byte[] blockBuffer = new byte[BLOCK_SIZE];
                    for (int blockIndex = 0; blockIndex < blockCount; blockIndex++)
                    {
                        string context = resumeInfo.Contexts[blockIndex];
                        if (string.IsNullOrEmpty(context))
                        { 
                            //check upload controller action before each chunk
                            while (true)
                            {
                                upCtrl = putExtra.UploadController();

                                if (upCtrl == UploadControllerAction.Aborted)
                                {
                                    result.Code = (int)HttpCode.USER_CANCELED;
                                    result.RefCode = (int)HttpCode.USER_CANCELED;
                                    result.RefText += string.Format("[{0}] [ResumableUpload] Info: upload task is aborted\n",
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"));
                                    manualResetEvent.Set();
                                    return result;
                                }
                                else if (upCtrl == UploadControllerAction.Suspended)
                                {
                                    result.RefCode = (int)HttpCode.USER_PAUSED;
                                    result.RefText += string.Format("[{0}] [ResumableUpload] Info: upload task is paused\n",
                                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"));
                                    manualResetEvent.WaitOne(1000);
                                }
                                else if (upCtrl == UploadControllerAction.Activated)
                                {
                                    break;
                                }
                            }

                            long offset = blockIndex * BLOCK_SIZE;
                            stream.Seek(offset, SeekOrigin.Begin);
                            int blockLen = stream.Read(blockBuffer, 0, BLOCK_SIZE);
                            byte[] blockData = new byte[blockLen];
                            Array.Copy(blockBuffer, blockData, blockLen);
                            blockDataDict.Add(blockIndex, blockData);

                            if (blockDataDict.Count == putExtra.BlockUploadThreads)
                            {
                                processMakeBlocks(blockDataDict, upToken, putExtra, resumeInfo, blockMakeResults, uploadedBytesDict, fileSize);
                                //check mkblk results
                                foreach(int blkIndex in blockMakeResults.Keys)
                                {
                                    HttpResult mkblkRet = blockMakeResults[blkIndex];
                                    if (mkblkRet.Code != 200)
                                    {
                                        result = mkblkRet;
                                        manualResetEvent.Set();
                                        return result;
                                    }
                                }
                                blockDataDict.Clear();
                                blockMakeResults.Clear();
                                if (!string.IsNullOrEmpty(putExtra.ResumeRecordFile))
                                {
                                    ResumeHelper.Save(resumeInfo, putExtra.ResumeRecordFile);
                                }
                            }
                        }
                    }

                    if (blockDataDict.Count > 0)
                    {
                        processMakeBlocks(blockDataDict, upToken, putExtra, resumeInfo, blockMakeResults, uploadedBytesDict, fileSize);
                        //check mkblk results
                        foreach (int blkIndex in blockMakeResults.Keys)
                        {
                            HttpResult mkblkRet = blockMakeResults[blkIndex];
                            if (mkblkRet.Code != 200)
                            {
                                result = mkblkRet;
                                manualResetEvent.Set();
                                return result;
                            }
                        }
                        blockDataDict.Clear();
                        blockMakeResults.Clear();
                        if (!string.IsNullOrEmpty(putExtra.ResumeRecordFile))
                        {
                            ResumeHelper.Save(resumeInfo, putExtra.ResumeRecordFile);
                        }
                    }

                    if (upCtrl == UploadControllerAction.Activated)
                    {
                        HttpResult hr = MakeFile(key, fileSize, key, upToken, putExtra, resumeInfo.Contexts);
                        if (hr.Code != (int)HttpCode.OK)
                        {
                            result.Shadow(hr);
                            result.RefText += string.Format("[{0}] [ResumableUpload] Error: mkfile: code = {1}, text = {2}\n",
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"), hr.Code, hr.Text);
                        }

                        if (File.Exists(putExtra.ResumeRecordFile))
                        {
                            File.Delete(putExtra.ResumeRecordFile);
                        }
                        result.Shadow(hr);
                        result.RefText += string.Format("[{0}] [ResumableUpload] Uploaded: \"{1}\" ==> \"{2}\"\n",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"), putExtra.ResumeRecordFile, key);
                    }
                    else
                    {
                        result.Code = (int)HttpCode.USER_CANCELED;
                        result.RefCode = (int)HttpCode.USER_CANCELED;
                        result.RefText += string.Format("[{0}] [ResumableUpload] Info: upload task is aborted, mkfile\n",
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"));
                    }

                    manualResetEvent.Set();
                    return result;
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.StackTrace);
                    StringBuilder sb = new StringBuilder();
                    sb.AppendFormat("[{0}] [ResumableUpload] Error: ", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"));
                    Exception e = ex;
                    while (e != null)
                    {
                        sb.Append(e.Message + " ");
                        e = e.InnerException;
                    }
                    sb.AppendLine();

                    result.RefCode = (int)HttpCode.USER_UNDEF;
                    result.RefText += sb.ToString();
                }
            }

            return result;
        }

        private void processMakeBlocks(Dictionary<int, byte[]> blockDataDict, string upToken,
            PutExtra putExtra, ResumeInfo resumeInfo, Dictionary<int, HttpResult> blockMakeResults,
            Dictionary<string,long> uploadedBytesDict, long fileSize)
        {
            int taskMax = blockDataDict.Count;
            ManualResetEvent[] doneEvents = new ManualResetEvent[taskMax];
            int eventIndex = 0;
            object progressLock = new object();
            foreach (int blockIndex in blockDataDict.Keys)
            {
                //signal task
                ManualResetEvent doneEvent = new ManualResetEvent(false);
                doneEvents[eventIndex] = doneEvent;
                eventIndex += 1;

                //queue task
                byte[] blockData = blockDataDict[blockIndex];
                ResumeBlocker resumeBlocker = new ResumeBlocker(doneEvent, blockData, blockIndex, upToken, putExtra,
                    resumeInfo, blockMakeResults, progressLock, uploadedBytesDict, fileSize);
                ThreadPool.QueueUserWorkItem(new WaitCallback(this.MakeBlock), resumeBlocker);
            }

            try
            {
                WaitHandle.WaitAll(doneEvents);
            }
            catch (Exception ex)
            {
                Console.WriteLine("wait all exceptions:" + ex.StackTrace);
                //pass
            }
        }

        /// <summary>
        /// 创建块(携带首片数据),同时检查CRC32
        /// </summary>
        /// <param name="resumeBlockerObj">创建分片上次的块请求</param>
        private void MakeBlock(object resumeBlockerObj)
        {
            ResumeBlocker resumeBlocker = (ResumeBlocker)resumeBlockerObj;
            ManualResetEvent doneEvent = resumeBlocker.DoneEvent;
            Dictionary<int, HttpResult> blockMakeResults = resumeBlocker.BlockMakeResults;
            PutExtra putExtra = resumeBlocker.PutExtra;
            int blockIndex = resumeBlocker.BlockIndex;
            HttpResult result = new HttpResult();
            //check whether to cancel
            while (true)
            {
                UploadControllerAction upCtl = resumeBlocker.PutExtra.UploadController();
                if (upCtl == UploadControllerAction.Suspended)
                {
                    doneEvent.WaitOne(1000);
                    continue;
                }
                else if (upCtl == UploadControllerAction.Aborted)
                {
                    doneEvent.Set();

                    result.Code = (int)HttpCode.USER_CANCELED;
                    result.RefCode = (int)HttpCode.USER_CANCELED;
                    result.RefText += string.Format("[{0}] [ResumableUpload] Info: upload task is aborted, mkblk {1}\n",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"),blockIndex);
                    blockMakeResults.Add(blockIndex,result);
                    return;
                }
                else
                {
                    break;
                }
            }

            byte[] blockBuffer = resumeBlocker.BlockBuffer;
            int blockSize = blockBuffer.Length;
           
            string upToken = resumeBlocker.UploadToken;
            Dictionary<string, long> uploadedBytesDict = resumeBlocker.UploadedBytesDict;
            long fileSize = resumeBlocker.FileSize;
            object progressLock = resumeBlocker.ProgressLock;
            ResumeInfo resumeInfo = resumeBlocker.ResumeInfo;

            try
            {
                //get upload host
                string ak = UpToken.GetAccessKeyFromUpToken(upToken);
                string bucket = UpToken.GetBucketFromUpToken(upToken);
                if (ak == null || bucket == null)
                {
                    result = HttpResult.InvalidToken;
                    doneEvent.Set();
                    return;
                }

                string uploadHost = this.config.UpHost(ak, bucket);

                string url = string.Format("{0}/mkblk/{1}", uploadHost, blockSize);
                string upTokenStr = string.Format("UpToken {0}", upToken);
                using (MemoryStream ms = new MemoryStream(blockBuffer, 0, blockSize))
                {
                    byte[] data = ms.ToArray();

                    result = httpManager.PostData(url, data, upTokenStr);

                    if (result.Code == (int)HttpCode.OK)
                    {
                        ResumeContext rc = JsonConvert.DeserializeObject<ResumeContext>(result.Text);

                        if (rc.Crc32 > 0)
                        {
                            uint crc_1 = rc.Crc32;
                            uint crc_2 = CRC32.CheckSumSlice(blockBuffer, 0, blockSize);
                            if (crc_1 != crc_2)
                            {
                                result.RefCode = (int)HttpCode.USER_NEED_RETRY;
                                result.RefText += string.Format(" CRC32: remote={0}, local={1}\n", crc_1, crc_2);
                            }
                            else
                            {
                                //write the mkblk context
                                resumeInfo.Contexts[blockIndex] = rc.Ctx;
                                resumeInfo.ExpiredAt = rc.ExpiredAt;
                                lock (progressLock)
                                {
                                    uploadedBytesDict["UploadProgress"] += blockSize;
                                }
                                putExtra.ProgressHandler(uploadedBytesDict["UploadProgress"], fileSize);
                            }
                        }
                        else
                        {
                            result.RefText += string.Format("[{0}] JSON Decode Error: text = {1}",
                                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"), result.Text);
                            result.RefCode = (int)HttpCode.USER_NEED_RETRY;
                        }
                    }
                    else
                    {
                        result.RefCode = (int)HttpCode.USER_NEED_RETRY;
                    }
                }
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("[{0}] mkblk Error: ", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"));
                Exception e = ex;
                while (e != null)
                {
                    sb.Append(e.Message + " ");
                    e = e.InnerException;
                }
                sb.AppendLine();

                if (ex is QiniuException)
                {
                    QiniuException qex = (QiniuException)ex;
                    result.Code = qex.HttpResult.Code;
                    result.RefCode = qex.HttpResult.Code;
                    result.Text = qex.HttpResult.Text;
                    result.RefText += sb.ToString();
                }
                else
                {
                    result.RefCode = (int)HttpCode.USER_UNDEF;
                    result.RefText += sb.ToString();
                }
            }

            //return the http result
            blockMakeResults.Add(blockIndex, result);
            doneEvent.Set();
        }

        /// <summary>
        /// 根据已上传的所有分片数据创建文件
        /// </summary>
        /// <param name="fileName">源文件名</param>
        /// <param name="size">文件大小</param>
        /// <param name="key">要保存的文件名</param>
        /// <param name="contexts">所有数据块的Context</param>
        /// <param name="upToken">上传凭证</param>
        /// <param name="putExtra">用户指定的额外参数</param>
        /// <returns>此操作执行后的返回结果</returns>
        private HttpResult MakeFile(string fileName, long size, string key, string upToken, PutExtra putExtra, string[] contexts)
        {
            HttpResult result = new HttpResult();

            try
            {
                string fnameStr = "fname";
                string mimeTypeStr = "";
                string keyStr = "";
                string paramStr = "";
                //check file name
                if (!string.IsNullOrEmpty(fileName))
                {
                    fnameStr = string.Format("/fname/{0}", Base64.UrlSafeBase64Encode(fileName));
                }

                //check mime type
                if (!string.IsNullOrEmpty(putExtra.MimeType))
                {
                    mimeTypeStr = string.Format("/mimeType/{0}", Base64.UrlSafeBase64Encode(putExtra.MimeType));
                }

                //check key
                if (!string.IsNullOrEmpty(key))
                {
                    keyStr = string.Format("/key/{0}", Base64.UrlSafeBase64Encode(key));
                }

                //check extra params
                if (putExtra.Params != null && putExtra.Params.Count > 0)
                {
                    StringBuilder sb = new StringBuilder();
                    foreach (var kvp in putExtra.Params)
                    {
                        string k = kvp.Key;
                        string v = kvp.Value;
                        if (k.StartsWith("x:") && !string.IsNullOrEmpty(v))
                        {
                            sb.AppendFormat("/{0}/{1}", k, v);
                        }
                    }

                    paramStr = sb.ToString();
                }

                //get upload host
                string ak = UpToken.GetAccessKeyFromUpToken(upToken);
                string bucket = UpToken.GetBucketFromUpToken(upToken);
                if (ak == null || bucket == null)
                {
                    return HttpResult.InvalidToken;
                }

                string uploadHost = this.config.UpHost(ak, bucket);

                string url = string.Format("{0}/mkfile/{1}{2}{3}{4}{5}", uploadHost, size, mimeTypeStr, fnameStr, keyStr, paramStr);
                string body = string.Join(",", contexts);
                string upTokenStr = string.Format("UpToken {0}", upToken);

                result = httpManager.PostText(url, body, upTokenStr);
            }
            catch (Exception ex)
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("[{0}] mkfile Error: ", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"));
                Exception e = ex;
                while (e != null)
                {
                    sb.Append(e.Message + " ");
                    e = e.InnerException;
                }
                sb.AppendLine();

                if (ex is QiniuException)
                {
                    QiniuException qex = (QiniuException)ex;
                    result.Code = qex.HttpResult.Code;
                    result.RefCode = qex.HttpResult.Code;
                    result.Text = qex.HttpResult.Text;
                    result.RefText += sb.ToString();
                }
                else
                {
                    result.RefCode = (int)HttpCode.USER_UNDEF;
                    result.RefText += sb.ToString();
                }
            }

            return result;
        }


        /// <summary>
        /// 默认的进度处理函数-上传文件
        /// </summary>
        /// <param name="uploadedBytes">已上传的字节数</param>
        /// <param name="totalBytes">文件总字节数</param>
        public static void DefaultUploadProgressHandler(long uploadedBytes, long totalBytes)
        {
            if (uploadedBytes < totalBytes)
            {
                Console.WriteLine("[{0}] [ResumableUpload] Progress: {1,7:0.000}%", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"), 100.0 * uploadedBytes / totalBytes);
            }
            else
            {
                Console.WriteLine("[{0}] [ResumableUpload] Progress: {1,7:0.000}%\n", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffff"), 100.0);
            }
        }

        /// <summary>
        /// 默认的上传控制函数，默认不执行任何控制
        /// </summary>
        /// <returns>控制状态</returns>
        public static UploadControllerAction DefaultUploadController()
        {
            return UploadControllerAction.Activated;
        }
    }
}
