using LibreLancer.World;
using Xunit;

namespace LibreLancer.Tests;

public class GameObjectComponentTests
{
    private abstract class IntermediateComponent(GameObject parent) : GameComponent(parent);
    private sealed class ConcreteComponent(GameObject parent) : IntermediateComponent(parent);

    [Fact]
    public void ComponentIsDiscoverableThroughAllBaseTypes()
    {
        var obj = new GameObject();
        var component = new ConcreteComponent(obj);

        obj.AddComponent(component);

        Assert.Same(component, obj.GetComponent<ConcreteComponent>());
        Assert.Same(component, obj.GetComponent<IntermediateComponent>());
    }

    [Fact]
    public void RemovingComponentClearsAllRegisteredBaseTypes()
    {
        var obj = new GameObject();
        var component = new ConcreteComponent(obj);
        obj.AddComponent(component);

        obj.RemoveComponent(component);

        Assert.Null(obj.GetComponent<ConcreteComponent>());
        Assert.Null(obj.GetComponent<IntermediateComponent>());
    }
}
