namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class GodDamageCommand : IConsoleCommand
{
    public string Name => "goddamage";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        player.GodDamage = !player.GodDamage;
        player.RpcClient.OnConsoleMessage($"God damage {(player.GodDamage ? "enabled" : "disabled")}");
    }
}
