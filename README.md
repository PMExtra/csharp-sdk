# Qiniu (Cloud) C# SDK

[![NuGet](https://img.shields.io/nuget/v/Qiniu.SDK.svg)](https://www.nuget.org/packages/Qiniu.SDK)

## 说明

这是由第三方 [PMExtra](https://github.com/PMExtra) 维护的 C# SDK 。

在官方 SDK 基础上：

1. 使用异步 API 访问网络（**注意：因此所有访问网络的 SDK 接口也是异步，这是接口上与官方 SDK 唯一不同的地方**）。
2. 不再支持过低版本的 .Net Framework，但支持以下平台
    - .Net Framework 4.5 及以上版本
    - .Net Standard 1.3 (.Net Core 1.1)
    - .Net Standard 2.0 (.Net Core 2.0 / 2.1)
    - 理论上支持其它 .Net Standard 的兼容平台，例如 Mono / Xamarin / UWP 等，尚未测试

## 安装

在支持平台上使用 Nuget 搜索安装 [`Qiniu.SDK`](https://www.nuget.org/packages/Qiniu.SDK) 即可。

## 使用

* 参考文档：[七牛云存储 C# SDK 使用指南](https://developer.qiniu.com/kodo/sdk/1237/csharp)
* 可以参考我们为大家精心准备的使用 [实例](https://github.com/qiniu/csharp-sdk/tree/master/src/QiniuTests)

## 贡献代码

1. Fork

2. 创建您的特性分支 git checkout -b my-new-feature

3. 提交您的改动 git commit -am 'Added some feature'

4. 将您的修改记录提交到远程 git 仓库 git push origin my-new-feature

5. 然后到 github 网站的该 git 远程仓库的 my-new-feature 分支下发起 Pull Request

