using LibreLancer.Data.GameData.Items;

namespace LibreLancer.Server.ConsoleCommands;

[ConsoleCommand]
public class GiveCommand : IConsoleCommand
{
    public string Name => "give";
    public bool Admin => true;

    public void Run(Player player, string arguments)
    {
        if (!ConsoleCommands.ParseString<string, int>(arguments, out var nickname, out var count) || count <= 0)
        {
            player.RpcClient.OnConsoleMessage("Invalid argument. Expecting [equipment] [count]");
            return;
        }

        var equipment = player.Game.GameData.Items.Equipment.Get(nickname);
        if (equipment == null)
        {
            player.RpcClient.OnConsoleMessage($"Equipment does not exist '{nickname}'");
            return;
        }

        using (var transaction = player.Character!.BeginTransaction())
            transaction.AddCargo(equipment, null, count);

        player.UpdateCurrentInventory();
        player.RpcClient.OnConsoleMessage($"Gave {count} {nickname}");
    }
}
