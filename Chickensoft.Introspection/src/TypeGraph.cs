namespace Chickensoft.Introspection;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

internal class TypeGraph : ITypeGraph {
  #region ITypeRegistry
  public IReadOnlyDictionary<Type, string> VisibleTypes => _visibleTypes;

  public IReadOnlyDictionary<Type, Func<object>> ConcreteVisibleTypes =>
    _concreteVisibleTypes;

  public IReadOnlyDictionary<Type, IMetatype> Metatypes => _metatypes;
  #endregion ITypeRegistry

  #region Caches
  private readonly Dictionary<Type, string> _visibleTypes = new();
  private readonly Dictionary<Type, Func<object>>
    _concreteVisibleTypes = new();
  private readonly Dictionary<Type, IMetatype> _metatypes = new();
  private readonly Dictionary<Type, HashSet<Type>> _typesByBaseType =
    new();
  private readonly Dictionary<Type, HashSet<Type>> _typesByAncestor =
    new();
  private readonly Dictionary<Type, List<PropertyMetadata>>
    _allPropertiesByType = new();
  private readonly Dictionary<Type, List<Attribute>> _allAttributesByType =
    new();
  private readonly Dictionary<string, Type> _introspectiveTypesById =
    new();
  #endregion Caches

  internal void Reset() {
    _visibleTypes.Clear();
    _concreteVisibleTypes.Clear();
    _metatypes.Clear();
    _typesByBaseType.Clear();
    _typesByAncestor.Clear();
    _allPropertiesByType.Clear();
    _allAttributesByType.Clear();
    _introspectiveTypesById.Clear();
  }

  #region ITypeGraph
  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public void Register(ITypeRegistry registry) {
    RegisterTypes(registry);
    ComputeTypesByBaseType(registry);
  }

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool IsVisibleFromGlobalScope(Type type) =>
    _visibleTypes.ContainsKey(type);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool IsConcrete(Type type) =>
    _concreteVisibleTypes.ContainsKey(type);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool IsIntrospectiveType(Type type) => _metatypes.ContainsKey(type);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public bool HasIntrospectiveType(string id) =>
    _introspectiveTypesById.ContainsKey(id);

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public Type GetIntrospectiveType(string id) =>
    _introspectiveTypesById[id];

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  public IMetatype GetMetatype(Type type) =>
    !Metatypes.TryGetValue(type, out var metatype)
      ? throw new KeyNotFoundException(
        $"Type {type} is not an introspective type. To make a type " +
        $"introspective, add the [{nameof(IntrospectiveAttribute)}] " +
        "to it so the introspection generator will generate a metatype " +
        "description of it."
      )
      : metatype;

  public ISet<Type> GetSubtypes(Type type) =>
    _typesByBaseType[type];

  public ISet<Type> GetDescendantSubtypes(Type type) {
    CacheDescendants(type);
    return _typesByAncestor[type];
  }

  public IEnumerable<PropertyMetadata> GetProperties(Type type) =>
    GetMetatypeAndBaseMetatypes(type)
      .SelectMany((t) => Metatypes[t].Properties)
      .Distinct();

  public IDictionary<Type, Attribute[]> GetAttributes(Type type) =>
    GetMetatypeAndBaseMetatypes(type)
      .SelectMany((t) => Metatypes[t].Attributes)
      .ToDictionary(
        keySelector: (typeToAttr) => typeToAttr.Key,
        elementSelector: (typeToAttr) => typeToAttr.Value
      );
  #endregion ITypeGraph

  #region Private Utilities

  [MethodImpl(MethodImplOptions.AggressiveInlining)]
  private void CacheDescendants(Type type) {
    if (_typesByAncestor.ContainsKey(type)) { return; }

    _typesByAncestor.Add(type, FindDescendants(type));
  }

  private HashSet<Type> FindDescendants(Type type) {
    var descendants = new HashSet<Type>();
    var queue = new Queue<Type>();
    queue.Enqueue(type);

    while (queue.Count > 0) {
      var currentType = queue.Dequeue();
      descendants.Add(currentType);

      if (_typesByBaseType.TryGetValue(currentType, out var children)) {
        foreach (var child in children) {
          queue.Enqueue(child);
        }
      }
    }

    descendants.Remove(type);

    return descendants;
  }

  private void RegisterTypes(ITypeRegistry registry) {
    // Iterate through all visible types in O(n) time and add them to our
    // internal caches.
    // Why do this? We want to allow multiple assemblies to use this system to
    // find types by their base type or ancestor.
    foreach (var type in registry.VisibleTypes.Keys) {
      _visibleTypes.Add(type, registry.VisibleTypes[type]);

      if (registry.ConcreteVisibleTypes.ContainsKey(type)) {
        _concreteVisibleTypes[type] =
          registry.ConcreteVisibleTypes[type];
      }

      if (registry.Metatypes.ContainsKey(type)) {
        _metatypes[type] = registry.Metatypes[type];
        _introspectiveTypesById[registry.Metatypes[type].Id] = type;
      }
    }
  }

  private void ComputeTypesByBaseType(ITypeRegistry registry) {
    // Iterate through each type in the registry and its base types,
    // constructing a flat map of base types to their immediately derived types.
    // The beauty of this approach is that it discovers base types which may be
    // in other modules, and works in reflection-free mode since BaseType is
    // always supported by every C# environment, even AOT environments.
    foreach (var type in registry.VisibleTypes.Keys) {
      var lastType = type;
      var baseType = type.BaseType;

      // As far as we know, any type could be a base type.
      if (!_typesByBaseType.ContainsKey(type)) {
        _typesByBaseType[type] = new HashSet<Type>();
      }

      while (baseType != null) {
        if (!_typesByBaseType.TryGetValue(baseType, out var existingSet)) {
          existingSet = new HashSet<Type>();
          _typesByBaseType.Add(baseType, existingSet);
        }
        existingSet.Add(lastType);

        lastType = baseType;
        baseType = lastType.BaseType;
      }
    }
  }

  /// <summary>
  /// Enumerates through a type and its base type hierarchy to discover all
  /// metatypes that describe the type and its base types.
  /// </summary>
  /// <param name="type">Type whose type hierarchy should be examined.</param>
  /// <returns>The type's metatype (if it has one), and any metatypes that
  /// describe its base types, in the order of the most derived type to the
  /// least derived type.</returns>
  private IEnumerable<Type> GetMetatypeAndBaseMetatypes(Type type) {
    var currentType = type;

    do {
      if (_metatypes.ContainsKey(currentType)) {
        yield return currentType;
      }

      currentType = currentType.BaseType;
    } while (currentType != null);
  }

  #endregion Private Utilities

}
