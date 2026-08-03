using System.Collections.Generic;
using LibreLancer.Data.GameData;
using LibreLancer.Data.GameData.Items;
using LibreLancer.Data.Schema.Equipment;
using LibreLancer.Server;
using LibreLancer.World;
using Xunit;

namespace LibreLancer.Tests;

public class CargoUtilitiesTests
{
    [Fact]
    public void DeployableAmmoUsesPerTypeAmmoLimit()
    {
        var mineAmmo = new MunitionEquip { Def = new Mine(), Volume = 0 };
        var otherAmmo = new MunitionEquip { Def = new Countermeasure(), Volume = 0 };
        var cargo = new List<NetCargo>
        {
            new() { Equipment = mineAmmo, Count = 47 },
            new() { Equipment = otherAmmo, Count = 50 }
        };

        var limit = CargoUtilities.GetItemLimit(cargo, new Ship(), mineAmmo);

        Assert.Equal(3, limit);
    }

    [Fact]
    public void AmmoLimitAlsoRespectsRemainingHoldSpace()
    {
        var mineAmmo = new MunitionEquip { Def = new Mine(), Volume = 2 };
        var cargo = new List<NetCargo>
        {
            new() { Equipment = mineAmmo, Count = 5 }
        };
        var ship = new Ship { HoldSize = 16 };

        var limit = CargoUtilities.GetItemLimit(cargo, ship, mineAmmo);

        Assert.Equal(3, limit);
    }
}
