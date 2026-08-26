using LibreLancer.Server.Components;
using LibreLancer.World.Components;

namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class SpeedCommand : IConsoleCommand
{
    public string Name => "speed";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        if (!ConsoleCommands.ParseString(arguments, out float speed) || speed <= 0)
        {
            player.RpcClient.OnConsoleMessage("Invalid argument. Expecting a positive cruise speed");
            return;
        }

        player.Space?.World?.EnqueueAction(() =>
        {
            var ship = player.Space.World.Players[player];
            if (!ship.TryGetComponent<SEngineComponent>(out var engine) ||
                !ship.TryGetComponent<ShipPhysicsComponent>(out var physics))
            {
                player.RpcClient.OnConsoleMessage("Could not find player engine");
                return;
            }

            engine.Engine.CruiseSpeed = speed;
            physics.CruiseSpeedOffset = 0;
            player.RpcClient.OnConsoleMessage($"Cruise speed set to {speed:0.###}");
        });
    }
}
