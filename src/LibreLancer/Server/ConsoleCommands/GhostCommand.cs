namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class GhostCommand : IConsoleCommand
{
    public string Name => "ghost";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        if (player.Space?.World == null)
        {
            player.RpcClient.OnConsoleMessage("Ghost command only works in space");
            return;
        }

        player.Space.World.EnqueueAction(() =>
        {
            var ship = player.Space!.World.Players[player];
            if (ship.PhysicsComponent?.Body == null)
            {
                player.RpcClient.OnConsoleMessage("Could not find player physics");
                return;
            }

            var enabled = ship.PhysicsComponent.Collidable;
            ship.PhysicsComponent.Collidable = !enabled;
            ship.PhysicsComponent.Body.Collidable = !enabled;
            player.RpcClient.OnConsoleMessage($"Ghost mode {(enabled ? "enabled" : "disabled")}");
        });
    }
}
