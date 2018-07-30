using System;
using Qiniu.Storage;
using Qiniu.Util;
using Xunit;

namespace Qiniu.Tests.Storage
{
    public class PutPolicyTests : TestEnv
    {
        [Fact]
        public void CreateUptokenTest()
        {
            var mac = new Mac(AccessKey, SecretKey);
            // 简单上传凭证
            var putPolicy = new PutPolicy
            {
                Scope = Bucket
            };
            var upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);

            // 自定义凭证有效期（示例2小时）
            putPolicy = new PutPolicy
            {
                Scope = Bucket
            };
            putPolicy.SetExpires(7200);
            upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);

            // 覆盖上传凭证
            putPolicy = new PutPolicy
            {
                Scope = $"{Bucket}:qiniu.png"
            };
            upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);

            // 自定义上传回复（非callback模式）凭证
            putPolicy = new PutPolicy
            {
                Scope = Bucket,
                ReturnBody = "{\"key\":\"$(key)\",\"hash\":\"$(etag)\",\"fsiz\":$(fsize),\"bucket\":\"$(bucket)\",\"name\":\"$(x:name)\"}"
            };
            upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);

            // 带回调业务服务器的凭证（application/json）
            putPolicy = new PutPolicy
            {
                Scope = Bucket,
                CallbackUrl = "http://api.example.com/qiniu/upload/callback",
                CallbackBody = "{\"key\":\"$(key)\",\"hash\":\"$(etag)\",\"fsiz\":$(fsize),\"bucket\":\"$(bucket)\",\"name\":\"$(x:name)\"}",
                CallbackBodyType = "application/json"
            };
            upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);

            // 带回调业务服务器的凭证（application/x-www-form-urlencoded）
            putPolicy = new PutPolicy
            {
                Scope = Bucket,
                CallbackUrl = "http://api.example.com/qiniu/upload/callback",
                CallbackBody = "key=$(key)&hash=$(etag)&bucket=$(bucket)&fsize=$(fsize)&name=$(x:name)"
            };
            upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);

            // 带数据处理的凭证
            putPolicy = new PutPolicy();
            var saveMp4Entry = Base64.UrlSafeBase64Encode(Bucket + ":avthumb_test_target.mp4");
            var saveJpgEntry = Base64.UrlSafeBase64Encode(Bucket + ":vframe_test_target.jpg");
            var avthumbMp4Fop = "avthumb/mp4|saveas/" + saveMp4Entry;
            var vframeJpgFop = "vframe/jpg/offset/1|saveas/" + saveJpgEntry;
            var fops = string.Join(";", avthumbMp4Fop, vframeJpgFop);
            putPolicy.Scope = Bucket;
            putPolicy.PersistentOps = fops;
            putPolicy.PersistentPipeline = "video-pipe";
            putPolicy.PersistentNotifyUrl = "http://api.example.com/qiniu/pfop/notify";
            upToken = Auth.CreateUploadToken(mac, putPolicy.ToJsonString());
            Console.WriteLine(upToken);
        }
    }
}
