namespace Chickensoft.LogicBlocks.Example;

public partial class VendingMachine {
  public record Idle : State,
  IGet<Input.SelectionEntered>, IGet<Input.PaymentReceived> {
    public Idle() {
      this.OnEnter(() => Output(new Output.ClearTransactionTimeOutTimer()));
    }

    public State On(in Input.SelectionEntered input) =>
      Get<VendingMachineStock>().HasItem(input.Type)
        ? new TransactionStarted(
          input.Type, Prices[input.Type], 0
        )
        : this;

    public State On(in Input.PaymentReceived input) {
      // Money was deposited with no selection — eject it right back.
      //
      // We could be evil and keep it, but we'd ruin our reputation as a
      // reliable vending machine in the office and then we'd never get ANY
      // money!
      Output(new Output.MakeChange(input.Amount));
      return this;
    }
  }
}
