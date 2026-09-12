using Microsoft.Extensions.Configuration;

namespace ETGDriverApp;

internal static class PackagedConfigurationExtensions
{
    // MAUI packages Resources/Raw/** as platform assets, not as files on a
    // path you can hand to AddJsonFile: on Android they live inside the APK
    // and on iOS inside the bundle. OpenAppPackageFileAsync is the only
    // portable way in, so the stream is read here and handed to
    // AddJsonStream.
    //
    // Blocking on the async call is deliberate and safe: configuration has to
    // exist before the container is built, this runs once on the startup
    // thread, and the read is from a local asset with no network or lock
    // involved. Do not copy this pattern for anything that can actually wait.
    public static IConfigurationBuilder AddPackagedJsonFile(
        this IConfigurationBuilder builder, string fileName, bool optional)
    {
        Stream stream;

        try
        {
            stream = FileSystem.OpenAppPackageFileAsync(fileName)
                .GetAwaiter()
                .GetResult();
        }
        catch (FileNotFoundException) when (optional)
        {
            return builder;
        }

        // AddJsonStream does not take ownership of the stream, and it reads
        // eagerly, so disposing straight after the call is correct.
        using (stream)
            return builder.AddJsonStream(stream);
    }
}
