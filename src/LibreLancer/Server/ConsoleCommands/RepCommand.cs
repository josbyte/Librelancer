using System;

namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class RepCommand : IConsoleCommand
{
    public string Name => "rep";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        if (!ConsoleCommands.ParseString<string, float>(arguments, out var nickname, out var reputation))
        {
            player.RpcClient.OnConsoleMessage("Invalid argument. Expecting [faction] [reputation]");
            return;
        }

        var faction = player.Game.GameData.Items.Factions.Get(nickname);
        if (faction == null)
        {
            player.RpcClient.OnConsoleMessage($"Faction does not exist '{nickname}'");
            return;
        }

        reputation = Math.Clamp(reputation, -1f, 1f);
        using (var transaction = player.Character!.BeginTransaction())
            transaction.UpdateReputation(faction, reputation);

        player.RpcClient.OnConsoleMessage($"Set {nickname} reputation to {reputation:0.###}");
    }
}
