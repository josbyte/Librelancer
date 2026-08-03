namespace LibreLancer.Data.GameData.Items;

public class MunitionEquip : Equipment
{
    public required Schema.Equipment.Munition Def;

    // Mines carry their explosion definition on the munition rather than on
    // the launcher.  Keeping the resolved definition here also lets the
    // server and clients use the same deployable spawn path.
    public Schema.Equipment.Explosion? Explosion;
    public ResolvedFx? ExplosionFx;

    //Fx Stuff
    public Schema.Effects.BeamSpear? ConstEffect_Spear;
    public Schema.Effects.BeamBolt? ConstEffect_Bolt;
}
