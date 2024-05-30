namespace Chickensoft.Introspection.Generator;

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Chickensoft.Introspection.Generator.Models;
using Chickensoft.Introspection.Generator.Utils;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// This generator exists to list types in the developer's codebase for use
/// with polymorphic serialization and deserialization or automatic state
/// creation and registration.
/// <br />
/// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/polymorphism?pivots=dotnet-8-0#configure-polymorphism-with-the-contract-model
/// <br />
/// Additionally, JSON Serialization can be tested by disabling Reflection:
/// https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation?pivots=dotnet-8-0#disable-reflection-defaults
/// <br />
/// For background on AOT/iOS Environments and STJ:
/// https://github.com/dotnet/runtime/issues/31326
/// </summary>
[Generator]
public class TypeGenerator : IIncrementalGenerator {
  public void Initialize(IncrementalGeneratorInitializationContext context) {
    // If you need to debug the source generator, uncomment the following line
    // and use Visual Studio 2022 on Windows to attach to debugging next time
    // the source generator process is started by running `dotnet build` in
    // the project consuming the source generator
    //
    // --------------------------------------------------------------------- //
    // System.Diagnostics.Debugger.Launch();
    // --------------------------------------------------------------------- //
    //
    // You can debug a source generator in Visual Studio on Windows by
    // simply uncommenting the Debugger.Launch line above.

    // Otherwise...
    // To debug on macOS with VSCode, you can pull open the command palette
    // and select "Debug: Attach to a .NET 5+ or .NET Core process"
    // (csharp.attachToProcess) and then search "VBCS" and select the
    // matching compiler process. Once it attaches, this will stop sleeping
    // and you're on your merry way!

    // --------------------------------------------------------------------- //
    // while (!System.Diagnostics.Debugger.IsAttached) {
    //   Thread.Sleep(100);
    // }
    // System.Diagnostics.Debugger.Break();
    // --------------------------------------------------------------------- //

    // Because of partial type declarations, we may need to combine some
    // type declarations into one.
    var incrementalGenerationData = context.SyntaxProvider.CreateSyntaxProvider(
      predicate: IsTypeCandidate,
      transform: ResolveDeclaredTypeInfo
    )
    .Collect()
    .Select((declaredTypes, _) => {
      var typesByFullName = declaredTypes
        .GroupBy((type) => type.FullNameOpen);

      var uniqueTypes = typesByFullName
        .Select(
          // Combine non-unique type entries together.
          group => group.Aggregate(
            (DeclaredType typeA, DeclaredType typeB) =>
              typeA.MergePartialDefinition(typeB)
          )
        )
        .OrderBy(type => type.FullNameOpen) // Sort for deterministic output
        .ToDictionary(
          g => g.FullNameOpen,
          g => g
        );

      var tree = new TypeResolutionTree();
      tree.AddDeclaredTypes(uniqueTypes);

      var visibleTypeFullNames = tree.GetVisibleTypes();

      var visibleTypesBuilder =
        ImmutableHashSet.CreateBuilder<DeclaredType>();

      // Build up relevant registries to enable convenient and performant
      // introspection.
      foreach (var type in uniqueTypes.Values) {
        if (visibleTypeFullNames.Contains(type.FullNameOpen)) {
          visibleTypesBuilder.Add(type);
        }
      }

      return new DeclaredTypeRegistry(
        allTypes: uniqueTypes.ToImmutableDictionary(),
        visibleTypes: visibleTypesBuilder.ToImmutable()
      );
    });

    context.RegisterSourceOutput(
      source: incrementalGenerationData,
      action: static (
        SourceProductionContext context,
        DeclaredTypeRegistry registry
      ) => {
        if (OutputMetatypesAndReportDiagnostics(context, registry)) {
          GenerateTypeRegistry(context, registry);
        }
      }
    );
  }

  public static bool OutputMetatypesAndReportDiagnostics(
    SourceProductionContext context,
    DeclaredTypeRegistry registry
  ) {
    var diagnostics = new List<Diagnostic>();
    // map of id's to versions to types
    var seenMetatypeIds = new Dictionary<string, DeclaredType>();

    // A metatype is a class generated by the source generator that contains
    // information about the class it is generated inside of.
    foreach (var type in registry.AllTypes.Values) {
      if (!type.HasIntrospectiveAttribute) {
        continue;
      }

      if (
        type.Kind is DeclaredTypeKind.AbstractType &&
        type.HasIntrospectiveAttribute &&
        type.HasVersionAttribute
      ) {
        // Abstract introspective types can't have versions.
        diagnostics.Add(
          Diagnostics.AbstractTypeHasVersion(
            type.SyntaxLocation,
            type.FullNameOpen,
            type
          )
        );
      }

      if (type.Kind is DeclaredTypeKind.ConcreteType && type.Version is < 1) {
        diagnostics.Add(
          Diagnostics.TypeHasInvalidVersion(
            type.SyntaxLocation,
            type.FullNameOpen,
            type
          )
        );
      }

      if (type.Id is { } id) {
        if (seenMetatypeIds.TryGetValue(id, out var other)) {
          // Metatype id's must be unique across all assemblies.
          diagnostics.Add(
            Diagnostics.TypeDoesNotHaveUniqueId(
              type.SyntaxLocation,
              type.FullNameClosed,
              type,
              other
            )
          );

          continue;
        }

        seenMetatypeIds[id] = type;
      }

      var invisibleTypes = type.ValidateTypeAndContainingTypes(
        registry.AllTypes, (t) => t.IsPublicOrInternal
      ).ToArray();

      if (invisibleTypes.Length > 0) {
        diagnostics.Add(
          Diagnostics.TypeNotVisible(
            type.SyntaxLocation,
            type.FullNameOpen,
            invisibleTypes
          )
        );
      }

      var nonPartialTypes = type.ValidateTypeAndContainingTypes(
        registry.AllTypes, (t) => t.Reference.IsPartial
      ).ToArray();

      if (nonPartialTypes.Length > 0) {
        diagnostics.Add(
          Diagnostics.TypeNotFullyPartial(
            type.SyntaxLocation,
            type.FullNameOpen,
            nonPartialTypes
          )
        );
      }

      var genericTypes = type.ValidateTypeAndContainingTypes(
        registry.AllTypes, (t) => t.Reference.TypeParameters.Length == 0
      ).ToArray();

      if (genericTypes.Length > 0) {
        diagnostics.Add(
          Diagnostics.TypeIsGeneric(
            type.SyntaxLocation,
            type.FullNameOpen,
            genericTypes
          )
        );
      }

      if (
        !registry.VisibleTypes.Contains(type) ||
        !type.CanGenerateMetatypeInfo ||
        type.Kind == DeclaredTypeKind.StaticClass ||
        type.Kind == DeclaredTypeKind.Interface
      ) {
        // Type is not visible from global scope or we're not able to generate
        // a metatype for it because it's generic or not fully partial, etc.
        continue;
      }

      var writer = CreateCodeWriter();

      WriteFileStart(writer);

      type.WriteMetatype(writer);

      WriteFileEnd(writer);

      context.AddSource(
        hintName: $"{type.Filename}.g.cs",
        source: writer.InnerWriter.ToString()
      );
    } // for each type

    foreach (var diagnostic in diagnostics) {
      context.ReportDiagnostic(diagnostic);
    }

    return !diagnostics.Any(d => d.Severity is DiagnosticSeverity.Error);
  }

  private static void WriteFileStart(IndentedTextWriter code) {
    code.WriteLine("#pragma warning disable");
    code.WriteLine("#nullable enable");
  }

  private static void WriteFileEnd(IndentedTextWriter code) {
    code.WriteLine("#nullable restore");
    code.WriteLine("#pragma warning restore");
  }

  public static void GenerateTypeRegistry(
    SourceProductionContext context,
    DeclaredTypeRegistry registry
  ) {
    var writer = CreateCodeWriter();

    WriteFileStart(writer);

    registry.Write(writer);

    WriteFileEnd(writer);

    context.AddSource(
      hintName: "TypeRegistry.g.cs",
      source: writer.InnerWriter.ToString()
    );
  }

  public static DeclaredType ResolveDeclaredTypeInfo(
    GeneratorSyntaxContext context, CancellationToken _
  ) {
    var typeDecl = (TypeDeclarationSyntax)context.Node;

    var name = typeDecl.Identifier.ValueText;
    var construction = GetConstruction(typeDecl);
    var isPartial = IsPartial(typeDecl);
    var typeParameters = GetTypeParameters(typeDecl);

    var reference = new TypeReference(
      SimpleName: name,
      Construction: construction,
      IsPartial: isPartial,
      TypeParameters: typeParameters
    );

    var location = GetLocation(typeDecl);
    var kind = GetKind(typeDecl);
    var isStatic = construction == Construction.StaticClass;
    var isTopLevelAccessible = IsTopLevelAccessible(typeDecl);

    var diagnostics = new HashSet<Diagnostic>();

    var usings = GetUsings(typeDecl);
    var properties = GetProperties(typeDecl);
    var attributes = GetAttributes(typeDecl.AttributeLists);
    var mixins = GetMixins(typeDecl);

    return new DeclaredType(
      Reference: reference,
      SyntaxLocation: typeDecl.GetLocation(),
      Location: location,
      Usings: usings,
      Kind: kind,
      IsStatic: isStatic,
      IsPublicOrInternal: isTopLevelAccessible,
      Properties: properties,
      Attributes: attributes,
      Mixins: mixins
    );
  }

  // We identify all type declarations and filter them out later by visibility
  // based on all the information about the type from any partial declarations
  // of the same type that we discover, as well as visibility information about
  // any containing types.
  public static bool IsTypeCandidate(SyntaxNode node, CancellationToken _) =>
      node is TypeDeclarationSyntax;

  public static DeclaredTypeKind GetKind(TypeDeclarationSyntax typeDecl) {
    if (typeDecl.Modifiers.Any(SyntaxKind.AbstractKeyword)) {
      // We know abstract types aren't interfaces or static classes.
      return DeclaredTypeKind.AbstractType;
    }
    if (typeDecl is ClassDeclarationSyntax classDecl) {
      return classDecl.Modifiers.Any(SyntaxKind.StaticKeyword)
        ? DeclaredTypeKind.StaticClass
        : DeclaredTypeKind.ConcreteType;
    }
    else if (typeDecl is InterfaceDeclarationSyntax) {
      return DeclaredTypeKind.Interface;
    }
    return DeclaredTypeKind.ConcreteType;
  }

  public static Construction GetConstruction(TypeDeclarationSyntax typeDecl) {
    if (typeDecl is ClassDeclarationSyntax classDecl) {
      return classDecl.Modifiers.Any(SyntaxKind.StaticKeyword)
        ? Construction.StaticClass
        : Construction.Class;
    }
    else if (typeDecl is InterfaceDeclarationSyntax) {
      return Construction.Interface;
    }
    else if (typeDecl is RecordDeclarationSyntax recordDecl) {
      return recordDecl.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword)
        ? Construction.RecordStruct
        : Construction.RecordClass;
    }
    return Construction.Struct;
  }

  public static ImmutableArray<string> GetTypeParameters(
    TypeDeclarationSyntax typeDecl
  ) =>
    typeDecl.TypeParameterList?.Parameters
      .Select(p => p.Identifier.ValueText)
      .ToImmutableArray()
      ?? ImmutableArray<string>.Empty;

  /// <summary>
  /// True if the type declaration is explicitly marked as visible at the
  /// top-level of the project. Doesn't check containing types, so this alone
  /// is not sufficient to determine overall visibility.
  /// </summary>
  /// <param name="typeDecl">Type declaration syntax.</param>
  /// <returns>True if marked as `public` or `internal`.</returns>
  public static bool IsTopLevelAccessible(TypeDeclarationSyntax typeDecl) =>
    typeDecl.Modifiers.Any(m =>
      m.IsKind(SyntaxKind.PublicKeyword) ||
      m.IsKind(SyntaxKind.InternalKeyword)
    );

  public static bool IsPartial(TypeDeclarationSyntax typeDecl) =>
    typeDecl.Modifiers.Any(SyntaxKind.PartialKeyword);

  /// <summary>
  /// Determines where a type is located within the source code.
  /// <br />
  /// https://stackoverflow.com/a/61409409
  /// </summary>
  /// <param name="source">Type declaration syntax.</param>
  /// <returns>Fully qualified name.</returns>
  /// <exception cref="ArgumentNullException />
  public static TypeLocation GetLocation(TypeDeclarationSyntax source) {
    var namespaces = new LinkedList<string>();
    var types = new LinkedList<TypeReference>();
    for (
      var parent = source.Parent; parent is not null; parent = parent.Parent
    ) {
      if (parent is BaseNamespaceDeclarationSyntax @namespace) {
        foreach (
          var namespacePart in @namespace.Name.ToString().Split('.').Reverse()
        ) {
          namespaces.AddFirst(namespacePart);
        }
      }
      else if (parent is TypeDeclarationSyntax type) {
        var typeParameters = type.TypeParameterList?.Parameters
            .Select(p => p.Identifier.ValueText)
            .ToImmutableArray()
            ?? ImmutableArray<string>.Empty;

        var construction = GetConstruction(type);
        var isPartial = IsPartial(type);

        var containingType = new TypeReference(
          SimpleName: type.Identifier.ValueText,
          Construction: construction,
          IsPartial: isPartial,
          TypeParameters: typeParameters
        );

        types.AddFirst(containingType);
      }
    }

    return new TypeLocation(
      namespaces.ToImmutableArray(),
      types.ToImmutableArray()
    );
  }

  public static ImmutableHashSet<UsingDirective> GetUsings(
    TypeDeclarationSyntax type
  ) {
    var allUsings = SyntaxFactory.List<UsingDirectiveSyntax>();
    foreach (var parent in type.Ancestors(false)) {
      if (parent is BaseNamespaceDeclarationSyntax ns) {
        allUsings = allUsings.AddRange(ns.Usings);
      }
      else if (parent is CompilationUnitSyntax comp) {
        allUsings = allUsings.AddRange(comp.Usings);
      }
    }
    return allUsings
      .Select(@using => new UsingDirective(
          Alias: @using.Alias?.Name.NormalizeWhitespace().ToString(),
          TypeName: @using.Name.NormalizeWhitespace().ToString(),
          IsGlobal: @using.GlobalKeyword is { ValueText: "global" },
          IsStatic: @using.StaticKeyword is { ValueText: "static" },
          IsAlias: @using.Alias != default
        )
      )
      .ToImmutableHashSet();
  }

  public static ImmutableArray<DeclaredProperty> GetProperties(
    TypeDeclarationSyntax type
  ) {
    var properties = ImmutableArray.CreateBuilder<DeclaredProperty>();
    foreach (var property in type.Members.OfType<PropertyDeclarationSyntax>()) {
      var propertyAttributes = GetAttributes(property.AttributeLists);

      // Never identified a situation in which the accessor list is null.
      var hasSetter = property.AccessorList!.Accessors
        .Any(accessor => accessor.IsKind(SyntaxKind.SetAccessorDeclaration));

      var isInit = property.AccessorList.Accessors
          .Any(
            accessor => accessor.IsKind(SyntaxKind.InitAccessorDeclaration)
          );

      hasSetter = hasSetter || isInit;

      var isNullable =
        property.Type is NullableTypeSyntax ||
        (
          property.Type is GenericNameSyntax generic &&
          generic.Identifier.ValueText == "Nullable"
        );

      var propType = property.Type;

      if (property.Type is NullableTypeSyntax nullableType) {
        propType = nullableType.ElementType;
      }

      var genericType = propType is GenericNameSyntax genericSyntax
        ? GenericTypeNode.Create(genericSyntax)
        : new GenericTypeNode(
          Type: propType.NormalizeWhitespace().ToString(),
          Children: ImmutableArray<GenericTypeNode>.Empty
        );

      properties.Add(
        new DeclaredProperty(
          Name: property.Identifier.ValueText,
          HasSetter: hasSetter,
          IsInit: isInit,
          IsNullable: isNullable,
          GenericType: genericType,
          Attributes: propertyAttributes
        )
      );
    }
    return properties.ToImmutable();
  }

  public static ImmutableArray<string> GetMixins(
    TypeDeclarationSyntax typeDecl
  ) {
    var mixins = ImmutableArray.CreateBuilder<string>();

    foreach (var attributeList in typeDecl.AttributeLists) {
      foreach (var attr in attributeList.Attributes) {
        if (attr.Name.ToString() == Constants.INTROSPECTIVE_ATTRIBUTE_NAME) {
          mixins.AddRange(
            attr.ArgumentList?.Arguments
              .Select(arg => arg.Expression)
              .OfType<TypeOfExpressionSyntax>()
              .Select(arg => arg.Type.NormalizeWhitespace().ToString())
              .ToImmutableArray() ?? ImmutableArray<string>.Empty
          );
        }
      }
    }

    return mixins.ToImmutable();
  }

  public static ImmutableArray<DeclaredAttribute> GetAttributes(
    SyntaxList<AttributeListSyntax> attributeLists
  ) {
    var attributes = ImmutableArray.CreateBuilder<DeclaredAttribute>();

    foreach (var attr in attributeLists) {
      foreach (var arg in attr.Attributes) {
        var initializerArgs = ImmutableArray.CreateBuilder<string>();
        var constructorArgs = ImmutableArray.CreateBuilder<string>();
        var name = arg.Name.NormalizeWhitespace().ToString();

        foreach (
          var argExpr in arg.ArgumentList?.Arguments ??
            SyntaxFactory.SeparatedList<AttributeArgumentSyntax>()
        ) {
          var argValue = argExpr.Expression.NormalizeWhitespace().ToString();

          // If it's a nameof() expression, go ahead and evaluate the equivalent
          // result with our regex implementation. Using the same nameof()
          // expression would be out of scope in the generated type registry,
          // since it doesn't share the using directives found in the type's
          // source file.
          argValue = argValue.StartsWith("nameof(")
            ? $"\"{Code.NameOf(argValue)}\""
            : argValue;

          if (argExpr.NameEquals is { } nameEquals) {
            initializerArgs.Add(
              $"{nameEquals.Name.NormalizeWhitespace()} = {argValue}"
            );
          }
          else {
            constructorArgs.Add(argValue);
          }
        }

        attributes.Add(new DeclaredAttribute(
          Name: name,
          ConstructorArgs: constructorArgs.ToImmutable(),
          InitializerArgs: initializerArgs.ToImmutable()
        ));
      }
    }

    return attributes.ToImmutable();
  }

  public static IndentedTextWriter CreateCodeWriter() =>
    new(new StringWriter(), "  ");
}
