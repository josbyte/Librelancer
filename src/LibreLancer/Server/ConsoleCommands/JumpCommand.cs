using System;
using System.Linq;

namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class JumpCommand : IConsoleCommand
{
    public string Name => "jump";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        if (!ConsoleCommands.ParseString(arguments, out string target))
        {
            player.RpcClient.OnConsoleMessage("Invalid argument. Expecting [target]");
            return;
        }

        var system = player.Game.GameData.Items.Systems.Get(player.System);
        if (system?.Objects.FirstOrDefault(x => x.Nickname.Equals(target, StringComparison.OrdinalIgnoreCase)) == null)
        {
            player.RpcClient.OnConsoleMessage($"Target does not exist '{target}'");
            return;
        }

        player.JumpTo(player.System, target, player.Space?.World.GatherJumpers() ?? []);
    }
}
