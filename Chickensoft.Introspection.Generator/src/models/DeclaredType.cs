namespace Chickensoft.Introspection.Generator.Models;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

/// <summary>
/// Represents a declared type.
/// </summary>
/// <param name="Reference">Type reference, including the name, construction,
/// type parameters, and whether or not the type is partial.</param>
/// <param name="SyntaxLocation">Syntax node location (used for diagnostics).
/// </param>
/// <param name="Location">Location of the type in the source code, including
/// namespaces and containing types.</param>
/// <param name="Usings">Using directives that are in scope for the type.
/// </param>
/// <param name="Kind">Kind of the type.</param>
/// <param name="IsStatic">True if the type is static. Static types can't
/// provide generic type retrieval.</param>
/// <param name="HasIntrospectiveAttribute">True if the type was tagged with the
/// MetatypeAttribute.</param>
/// <param name="HasMixinAttribute">True if the type is tagged with the mixin
/// attribute.</param>
/// <param name="IsTopLevelAccessible">True if the public or internal
/// visibility modifier was seen on the type.</param>
/// <param name="Properties">Properties declared on the type.</param>
/// <param name="Attributes">Attributes declared on the type.</param>
/// <param name="Mixins">Mixins that are applied to the type.</param>
public record DeclaredType(
  TypeReference Reference,
  Location SyntaxLocation,
  TypeLocation Location,
  ImmutableHashSet<UsingDirective> Usings,
  DeclaredTypeKind Kind,
  bool IsStatic,
  bool HasIntrospectiveAttribute,
  bool HasMixinAttribute,
  bool IsTopLevelAccessible,
  ImmutableArray<DeclaredProperty> Properties,
  ImmutableArray<DeclaredAttribute> Attributes,
  ImmutableArray<string> Mixins
) {
  /// <summary>Output filename (only works for non-generic types).</summary>
  public string Filename => FullName.Replace('.', '_');

  /// <summary>
  /// Fully qualified name, as determined based on syntax nodes only.
  /// </summary>
  public string FullName =>
    Location.Prefix + Reference.Name + Reference.OpenGenerics;

  /// <summary>
  /// True if the metatype information can be generated for this type.
  /// </summary>
  public bool CanGenerateMetatypeInfo =>
    HasIntrospectiveAttribute &&
    Location.IsFullyPartialOrTopLevel &&
    !IsGeneric;

  /// <summary>
  /// True if the type is generic. A type is generic if it has type parameters
  /// or is nested inside any containing types that have type parameters.
  /// </summary>
  public bool IsGeneric =>
    Reference.TypeParameters.Length > 0 ||
    Location.IsInGenericType;

  /// <summary>
  /// Identifier of the type. Types tagged with the [Meta] attribute can also
  /// be tagged with the optional [Id] attribute, which allows a custom string
  /// identifier to be given as the type's id.
  /// </summary>
  public string? Id => IdAttribute?.Value?.ConstructorArgs.FirstOrDefault();

  /// <summary>
  /// Whether or not the declared type was given a specific identifier.
  /// </summary>
  public bool HasId => IdAttribute.Value is not null;

  /// <summary>
  /// Validates that the DeclaredType of this type and its containing types
  /// satisfy the given predicate. Returns a list of types that do not satisfy
  /// the predicate.
  /// </summary>
  /// <param name="allTypes">Table of type full names with open generics to
  /// the declared type they represent.</param>
  /// <param name="predicate">Predicate each type must satisfy.</param>
  /// <returns>Enumerable of types that do not satisfy the predicate.</returns>
  public IEnumerable<DeclaredType> ValidateTypeAndContainingTypes(
    IDictionary<string, DeclaredType> allTypes,
    Func<DeclaredType, bool> predicate
  ) {
    // Have to reconstruct the full names of the containing types from our
    // type reference and location information.
    var fullName = Location.Namespace;
    var containingTypeFullNames = new Dictionary<TypeReference, string>();

    foreach (var containingType in Location.ContainingTypes) {
      fullName +=
        (fullName.Length == 0 ? "" : ".") +
        containingType.NameWithOpenGenerics;

      containingTypeFullNames[containingType] = fullName;
    }

    var typesToValidate =
      new[] { this }.Concat(Location.ContainingTypes.Select(
        (typeRef) => allTypes[containingTypeFullNames[typeRef]]
      )
    );

    return typesToValidate.Where((type) => !predicate(type));
  }

  private Lazy<DeclaredAttribute?> IntrospectiveAttribute { get; } = new(
    () => Attributes
      .FirstOrDefault(
        (attr) => attr.Name == Constants.INTROSPECTIVE_ATTRIBUTE_NAME
      )
    );

  private Lazy<DeclaredAttribute?> IdAttribute { get; } = new(
    () => Attributes
      .FirstOrDefault((attr) => attr.Name == Constants.ID_ATTRIBUTE_NAME)
    );

  /// <summary>
  /// Merge this partial type definition with another partial type definition
  /// for the same type.
  /// </summary>
  /// <param name="declaredType">Declared type representing the same type.
  /// </param>
  /// <returns>Updated representation of the declared type.</returns>
  public DeclaredType MergePartialDefinition(
    DeclaredType declaredType
  ) => new(
    Reference.MergePartialDefinition(declaredType.Reference),
    HasIntrospectiveAttribute ? SyntaxLocation : declaredType.SyntaxLocation,
    Location,
    Usings.Union(declaredType.Usings),
    Kind,
    IsStatic || declaredType.IsStatic,
    HasIntrospectiveAttribute || declaredType.HasIntrospectiveAttribute,
    HasMixinAttribute || declaredType.HasMixinAttribute,
    IsTopLevelAccessible || declaredType.IsTopLevelAccessible,
    Properties
      .ToImmutableHashSet()
      .Union(declaredType.Properties)
      .ToImmutableArray(),
    Attributes.Concat(declaredType.Attributes).ToImmutableArray(),
    Mixins.Concat(declaredType.Mixins).ToImmutableArray()
  );
}
