namespace Chickensoft.SourceGeneratorUtils;

using System.Collections.Generic;
using System.Collections.Immutable;

public class Constants {
  /// <summary>Spaces per tab. Adjust to your generator's liking.</summary>
  public static int SPACES_PER_INDENT = 2;

  public const string DISABLE_CSPROJ_PROP = "LogicBlocksDiagramGeneratorDisabled";
  public const string LOGIC_BLOCK_GET_INITIAL_STATE = "GetInitialState";
  public const string LOGIC_BLOCK_STATE_OUTPUT = "Output";
  public const string LOGIC_BLOCK_STATE_LOGIC_ON_ENTER = "OnEnter";
  public const string LOGIC_BLOCK_STATE_LOGIC_ON_EXIT = "OnExit";
  public const string LOGIC_BLOCK_INPUT_INTERFACE_ID = "global::Chickensoft.LogicBlocks.LogicBlock.IGet";
  public const string LOGIC_BLOCK_ATTRIBUTE_NAME = "LogicBlock";
  public const string LOGIC_BLOCK_ATTRIBUTE_NAME_FULL = "LogicBlockAttribute";

  /// <summary>
  /// A dictionary of source code that must be injected into the compilation
  /// regardless of whether or not the user has taken advantage of any of the
  /// other features of this source generator.
  /// </summary>
  public static readonly ImmutableDictionary<string, string>
    PostInitializationSources = new Dictionary<string, string>() { }.ToImmutableDictionary();
}
