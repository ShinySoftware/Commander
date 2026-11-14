using ShinyCommander.Core.Remote;

namespace ShinyCommander.Remote;

public static class RemoteProviders
{
    /// <summary>Every protocol this build can open.</summary>
    public static IEnumerable<IRemoteProvider> All() =>
    [
        new SftpProvider(),
        new FtpProvider(),
        new WebDavProvider(),
        new S3Provider(),
        new AzureBlobProvider()
    ];
}
