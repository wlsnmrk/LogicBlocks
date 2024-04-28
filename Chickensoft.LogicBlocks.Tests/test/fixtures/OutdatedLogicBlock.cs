namespace Chickensoft.LogicBlocks.Tests.Fixtures;

using Chickensoft.Collections;
using Chickensoft.Introspection;
using Chickensoft.Serialization;

[Introspective("outdated_logic_block")]
[LogicBlock(typeof(State), Diagram = false)]
public partial class OutdatedLogicBlock :
LogicBlock<OutdatedLogicBlock.IState> {
  public override IState GetInitialState() => new V1();

  public interface IState : IStateLogic<IState>;

  [Introspective("outdated_logic_block_state")]
  public abstract partial record State : StateLogic<IState>, IState;

  [Introspective("outdated_logic_block_state_v1")]
  public partial record V1 : State, IOutdated {
    public object Upgrade(IReadOnlyBlackboard blackboard) => new V2();
  }

  [Introspective("outdated_logic_block_state_v2")]
  public partial record V2 : State, IOutdated {
    public object Upgrade(IReadOnlyBlackboard blackboard) => new V3();
  }

  [Introspective("outdated_logic_block_state_v3")]
  public partial record V3 : State;
}
