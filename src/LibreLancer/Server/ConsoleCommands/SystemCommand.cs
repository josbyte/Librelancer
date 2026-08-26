using System;
using System.Linq;

namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class SystemCommand : IConsoleCommand
{
    public string Name => "system";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        var parts = arguments.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            player.RpcClient.OnConsoleMessage("Invalid argument. Expecting [system] [target]");
            return;
        }

        var system = player.Game.GameData.Items.Systems.Get(parts[0]);
        if (system == null)
        {
            player.RpcClient.OnConsoleMessage($"System does not exist '{parts[0]}'");
            return;
        }

        var target = parts.Length == 2
            ? system.Objects.FirstOrDefault(x => x.Nickname.Equals(parts[1], StringComparison.OrdinalIgnoreCase))
            : system.Objects.FirstOrDefault(x => x.Archetype?.CanVisit == true);
        if (target == null)
        {
            player.RpcClient.OnConsoleMessage(parts.Length == 2
                ? $"Target does not exist '{parts[1]}'"
                : $"System '{parts[0]}' has no visitable target");
            return;
        }

        player.JumpTo(system.Nickname, target.Nickname, player.Space?.World.GatherJumpers() ?? []);
    }
}
