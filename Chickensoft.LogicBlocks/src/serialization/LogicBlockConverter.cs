namespace Chickensoft.LogicBlocks.Serialization;

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Chickensoft.Collections;
using Chickensoft.Serialization;

// Logic blocks JSON representation. Note that the blackboard object is dynamic.
// Each key represents the introspective type id of the type of object that
// follows. This allows us to serialize and deserialize arbitrary properties of
// the blackboard, since the blackboard guarantees there will only be one of
// each type of object that is set on it.
//
// ```json
// {
//   "$type": "logic_block_introspective_type_id",
//   "state": {
//     "$type": "state_introspective_type_id"
//   },
//   "blackboard": {
//     "object_introspective_type_id": {}
//   }
// }
// ```
//
// Serialization based on https://tinyurl.com/deserialize-subsections-json

/// <inheritdoc />
public class LogicBlockConverter :
JsonConverter<object>, IIntrospectiveTypeConverter {
  /// <summary>Json type info resolver.</summary>
  public IJsonTypeInfoResolver Resolver { get; }

  /// <inheritdoc />
  public IReadOnlyBlackboard DependenciesBlackboard { get; }

  /// <summary>
  /// Create a new logic block converter with the given type info resolver.
  /// </summary>
  /// <param name="resolver">Json type info resolver.</param>
  /// <param name="dependenciesBlackboard">Dependencies that might be needed
  /// by outdated states to upgrade themselves.</param>
  public LogicBlockConverter(
    IJsonTypeInfoResolver resolver,
    IReadOnlyBlackboard dependenciesBlackboard
  ) {
    Resolver = resolver;
    DependenciesBlackboard = dependenciesBlackboard;
  }

  private string TypeDiscriminator =>
    IntrospectiveTypeResolver.TYPE_DISCRIMINATOR;

  private const string STATE_PROPERTY = "state";
  private const string BLACKBOARD_PROPERTY = "blackboard";

  /// <inheritdoc />
  public override bool CanConvert(Type typeToConvert) =>
    // We can only convert logic blocks that are also marked with the
    // [Introspective] attribute to make them serializable.
    Introspection.Types.Graph
      .GetDescendantSubtypes(typeof(LogicBlockBase))
      .Contains(typeToConvert) &&
    Introspection.Types.Graph.IsIntrospectiveType(typeToConvert);

  /// <inheritdoc />
  public override object? Read(
    ref Utf8JsonReader reader,
    Type typeToConvert,
    JsonSerializerOptions options
  ) {
    var json = JsonNode.Parse(ref reader) ?? throw new JsonException(
      $"Failed to parse JSON for logic block {typeToConvert}."
    );

    var typeId =
      json[TypeDiscriminator]?.ToString() ?? throw new JsonException(
        $"Logic block {typeToConvert} is missing its `{TypeDiscriminator}` " +
        "property."
      );

    var stateValue =
      json[STATE_PROPERTY]?.AsObject() ?? throw new JsonException(
        $"Logic block {typeToConvert} has an invalid `{STATE_PROPERTY}` value."
      );

    var stateId = stateValue[TypeDiscriminator]?.ToString() ??
      throw new JsonException(
        $"Logic block {typeToConvert}'s state is missing its " +
        $"`{TypeDiscriminator}` property."
      );

    var blackboard =
      json[BLACKBOARD_PROPERTY] ?? throw new JsonException(
        $"Logic block {typeToConvert} is missing its `{BLACKBOARD_PROPERTY}` " +
        "property."
      );

    if (!Introspection.Types.Graph.HasIntrospectiveType(typeId)) {
      throw new JsonException(
        $"Logic block {typeToConvert} has an unknown introspective type id " +
        $"`{typeId}`."
      );
    }

    if (!Introspection.Types.Graph.HasIntrospectiveType(stateId)) {
      throw new JsonException(
        $"Logic block {typeToConvert} has an unknown introspective state " +
        $"type id `{stateId}`."
      );
    }

    var blackboardObjects = new List<object>();

    // Create blackboard objects

    foreach (var member in blackboard.AsObject()) {
      if (!Introspection.Types.Graph.HasIntrospectiveType(member.Key)) {
        throw new JsonException(
          $"Logic block {typeToConvert} has an unknown introspective type id " +
          $"`{member.Key}` in its blackboard."
        );
      }

      var blackboardObjType =
        Introspection.Types.Graph.GetIntrospectiveType(member.Key);

      var blackboardObjTypeInfo =
        Resolver.GetTypeInfo(blackboardObjType, options) ??
        throw new JsonException(
          $"Failed to get type info for blackboard object {blackboardObjType}."
        );

      // Deserialize the blackboard object.
      var blackboardObject = member.Value.Deserialize(
        blackboardObjType, options
      ) ?? throw new JsonException(
        $"Failed to deserialize blackboard object {blackboardObjType}."
      );

      blackboardObjects.Add(blackboardObject);
    }

    // Create logic block
    var type = Introspection.Types.Graph.GetIntrospectiveType(typeId);
    var logicBlock = Introspection.Types.Graph.ConcreteVisibleTypes[type]();

    // Set blackboard values
    foreach (var blackboardObj in blackboardObjects) {
      ((LogicBlockBase)logicBlock)._blackboard.OverwriteObject(
        blackboardObj.GetType(), blackboardObj
      );
    }

    // Create state
    var stateType = Introspection.Types.Graph.GetIntrospectiveType(stateId);

    // We can force-unwrap state since we verified that it is a json object.
    var state = stateValue.Deserialize(
      stateType, options
    )!;

    if (state is IOutdated) {
      // Create a temporary blackboard that merges all the values from the
      // logic block's blackboard (that we just deserialized) and the
      // values from the upgrade blackboard that is given to outdated objects
      // when they are upgraded.
      var tempBlackboard = new Blackboard();

      blackboardObjects
        .ForEach((obj) => tempBlackboard.OverwriteObject(obj.GetType(), obj));

      foreach (var upgradeObjType in DependenciesBlackboard.Types) {
        var upgradeObj = DependenciesBlackboard.GetObject(upgradeObjType);
        tempBlackboard.OverwriteObject(upgradeObj.GetType(), upgradeObj);
      }

      // If state is outdated, keep upgrading it until it's not.
      // Otherwise, loop forever.
      while (state is IOutdated outdated) {
        state = outdated.Upgrade(tempBlackboard);
      }
    }

    // Set the state to be used (instead of the logic block's initial state)
    // whenever the logic block is started.
    ((LogicBlockBase)logicBlock).RestoreState(state);

    return logicBlock;
  }

  /// <inheritdoc />
  public override void Write(
    Utf8JsonWriter writer,
    object value,
    JsonSerializerOptions options
  ) {
    var logicBlock = (LogicBlockBase)value;

    var type = logicBlock.GetType();
    var typeId = Introspection.Types.Graph.GetMetatype(type).Id;

    writer.WriteStartObject();
    writer.WriteString(TypeDiscriminator, typeId);

    var state = logicBlock.ValueAsPlainObject;
    var stateType = state.GetType();
    var stateId = Introspection.Types.Graph.GetMetatype(stateType).Id;

    writer.WritePropertyName(STATE_PROPERTY);

    // Serialize to a JsonNode instead of directly to the writer so that we can
    // ensure the type discriminator always shows up. We can safely unwrap
    // since we know logic block states are not null and will always be
    // serialized to a JsonObject since there are constraints on what can be
    // a state, forcing it to be a record or a class.
    var stateNode = (JsonObject)JsonSerializer.SerializeToNode(
      state,
      state.GetType(),
      options
    )!;

    if (!stateNode.ContainsKey(TypeDiscriminator)) {
      // Always serialize the type discriminator, even for states that don't
      // have any derived types (for readability).
      stateNode.Add(TypeDiscriminator, stateId);
    }

    // Write state
    stateNode.WriteTo(writer);

    writer.WritePropertyName(BLACKBOARD_PROPERTY);
    writer.WriteStartObject();

    foreach (var blackboardObjType in logicBlock._blackboard.SavedTypes) {
      var blackboardObjTypeId =
        Introspection.Types.Graph.GetMetatype(blackboardObjType).Id;

      var blackboardObj =
        logicBlock._blackboard.GetObject(blackboardObjType);

      writer.WritePropertyName(blackboardObjTypeId);

      JsonSerializer.Serialize(
        writer,
        blackboardObj,
        blackboardObjType,
        options
      );
    }

    writer.WriteEndObject(); // Blackboard
    writer.WriteEndObject(); // LogicBlock
  }
}
