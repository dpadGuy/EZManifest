using EZManifest.Models;

namespace EZManifest.Services;

public sealed class GameInstallSizeService
{
    private readonly SteamDepotMetadataService _depotMetadata;

    public GameInstallSizeService(SteamDepotMetadataService depotMetadata) =>
        _depotMetadata = depotMetadata;

    public async Task<long?> ResolveAsync(GameEntry game, CancellationToken cancellationToken = default)
    {
        if (game.IsInstalled
            && !string.IsNullOrWhiteSpace(game.InstallPath)
            && Directory.Exists(game.InstallPath))
        {
            long folder = await Task.Run(() => MeasureFolder(game.InstallPath), cancellationToken)
                .ConfigureAwait(false);
            if (folder > 0)
                return folder;
        }

        if (string.IsNullOrWhiteSpace(game.AppId))
            return game.InstallSizeBytes is > 0 ? game.InstallSizeBytes : null;

        long? estimated = await _depotMetadata
            .EstimateWindowsInstallSizeAsync(game.AppId, cancellationToken)
            .ConfigureAwait(false);
        if (estimated is > 0)
            return estimated;

        return game.InstallSizeBytes is > 0 ? game.InstallSizeBytes : null;
    }

    private static long MeasureFolder(string path)
    {
        long total = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (Exception)
                {
                }
            }
        }
        catch (Exception)
        {
        }

        return total;
    }
}
