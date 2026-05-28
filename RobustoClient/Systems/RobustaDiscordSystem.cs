using Robust.Client.UserInterface;

namespace RobustoClient.Systems;

public class RobustaDiscordSystem : EntitySystem
{
    private const string DiscordUrl = "https://discord.gg/BKucu6uFUH";
    private const string FilePath = "arbc_ds_ws_pnd";
    [Dependency] private readonly IUriOpener _uri = default!;

    public void OpenDiscord()
    {
        _uri.OpenUri(DiscordUrl);
    }

    public override void Initialize()
    {
        if (File.Exists(FilePath))
            return;
        OpenDiscord();
        File.WriteAllText(FilePath, DiscordUrl);
    }
}