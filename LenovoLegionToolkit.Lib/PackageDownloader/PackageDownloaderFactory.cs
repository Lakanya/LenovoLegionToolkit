using System;

namespace LenovoLegionToolkit.Lib.PackageDownloader;

public class PackageDownloaderFactory(
    PCSupportPackageDownloader pcSupportPackageDownloader,
    VantagePackageDownloader vantagePackageDownloader,
    SmartPackageDownloader smartPackageDownloader)
{
    public enum Type
    {
        Smart,
        PCSupport,
        Vantage,
    }

    public IPackageDownloader GetInstance(Type type) => type switch
    {
        Type.Smart => smartPackageDownloader,
        Type.PCSupport => pcSupportPackageDownloader,
        Type.Vantage => vantagePackageDownloader,
        _ => throw new InvalidOperationException(nameof(type)),
    };
}
