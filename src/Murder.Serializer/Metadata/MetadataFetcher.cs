using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Murder.Serializer.Extensions;
using Murder.Serializer.Metadata;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace Murder.Generator.Metadata;

public readonly struct MetadataType
{
    public ITypeSymbol Type { get; init; } = default!;

    public string QualifiedName { get; init; } = string.Empty;

    public MetadataType() { }
}

public readonly struct ComplexDictionaryArguments
{
    public ITypeSymbol Key { get; init; }
    public ITypeSymbol Value { get; init; }
}

public enum ScanMode
{
    GenerateContextOnly = 1,
    GenerateOptions = 2
}

public sealed class MetadataFetcher
{
    private readonly Compilation _compilation;
    private readonly ReferencedAssemblyTypeFetcher _referencedAssemblyTypeFetcher;

    public readonly ScanMode Mode = ScanMode.GenerateContextOnly;

    public readonly HashSet<string> SerializableTypes = new();
    private readonly HashSet<ITypeSymbol> _typesThatWereScannedForPrivateFields = new(SymbolEqualityComparer.Default);

    public readonly HashSet<ComplexDictionaryArguments> ComplexDictionaries = new(DictionaryKeyTypesComparer.Default);
    public readonly Dictionary<ITypeSymbol, HashSet<MetadataType>> PolymorphicTypes = new(SymbolEqualityComparer.Default);

    private readonly HashSet<ITypeSymbol> _polymorphicTypesToLookForImplementation = new(SymbolEqualityComparer.Default);

    /// <summary>
    /// If this inherits from another Murder project, we will need to join the symbols.
    /// </summary>
    public INamedTypeSymbol? ParentContext { get; private set; } = null;

    public MetadataFetcher(Compilation compilation, string? parentAssembly)
    {
        _compilation = compilation;
        _referencedAssemblyTypeFetcher = new(compilation, parentAssembly);

        if (parentAssembly is not null)
        {
            Mode = ScanMode.GenerateOptions;
        }
    }

    internal void Populate(
        MurderTypeSymbols symbols,
        ImmutableArray<TypeDeclarationSyntax> potentialStructs,
        ImmutableArray<ClassDeclarationSyntax> potentialClasses)
    {
        ParentContext = FetchParentContext(symbols);

        // Gets all potential components/messages from the assembly this generator is processing.
        List<INamedTypeSymbol> structs = new();
        foreach (TypeDeclarationSyntax t in potentialStructs)
        {
            if (ValueTypeFromTypeDeclarationSyntax(t) is not INamedTypeSymbol symbol)
            {
                continue;
            }

            structs.Add(symbol);
        }

        TrackRootSerializables(symbols);

        PopulateFromParentAssembly(symbols);
        Populate(symbols, structs, potentialClasses.Select(GetTypeSymbol));
    }

    private void PopulateFromParentAssembly(
        MurderTypeSymbols symbols)
    {
        if (ParentContext is null || Mode is not ScanMode.GenerateOptions)
        {
            return;
        }

        List<INamedTypeSymbol> serializableTypesFromParent =
            _referencedAssemblyTypeFetcher.FindAllDeclaredSerializableAttributeTypes(symbols, ParentContext);

        foreach (INamedTypeSymbol t in serializableTypesFromParent)
        {
            if (t.IsValueType && !t.IsGenericType && t.ImplementsInterface(symbols.ComponentInterface))
            {
                MetadataType m = new() { Type = t, QualifiedName = t.FullyQualifiedName() };
                TrackPolymorphicType(symbols.ComponentInterface, m);

                _typesThatWereScannedForPrivateFields.Add(t);
            }

            if (t.IsValueType && !t.IsGenericType && t.ImplementsInterface(symbols.MessageInterface))
            {
                MetadataType m = new() { Type = t, QualifiedName = t.FullyQualifiedName() };
                TrackPolymorphicType(symbols.MessageInterface, m);

                _typesThatWereScannedForPrivateFields.Add(t);
            }

            if (t.IsValueType && !t.IsGenericType && t.ImplementsInterface(symbols.InteractionInterface))
            {
                string name = t.FullyQualifiedName();

                MetadataType m = new()
                {
                    Type = t,
                    QualifiedName = $"Bang.Interactions.InteractiveComponent<{name}>"
                };

                TrackPolymorphicType(symbols.ComponentInterface, m);
                TrackPolymorphicType(symbols.InteractiveComponentInterface, m);

                _typesThatWereScannedForPrivateFields.Add(t);
            }

            if (t.IsValueType && !t.IsAbstract && t.ImplementsInterface(symbols.StateMachineClass))
            {
                string name = t.FullyQualifiedName();

                MetadataType m = new()
                {
                    Type = t,
                    QualifiedName = $"Bang.StateMachines.StateMachineComponent<{name}>"
                };

                TrackPolymorphicType(symbols.ComponentInterface, m);
                TrackPolymorphicType(symbols.StateMachineComponentInterface, m);

                _typesThatWereScannedForPrivateFields.Add(t);
            }

            if (!t.IsValueType && t.IsSubtypeOf(symbols.GameAssetClass))
            {
                MetadataType m = new() { Type = t, QualifiedName = t.FullyQualifiedName() };

                TrackPolymorphicType(symbols.GameAssetClass, m);
            }

            if (IsPolymorphicCandidate(symbols, t))
            {
                _polymorphicTypesToLookForImplementation.Add(t);
            }

            if (t.IsGenericType && t.ConstructedFrom.Equals(symbols.ComplexDictionaryClass, SymbolEqualityComparer.Default))
            {
                ComplexDictionaryArguments args = new() { Key = t.TypeArguments[0], Value = t.TypeArguments[1] };
                ComplexDictionaries.Add(args);
            }
        }

        // We need to check for our own types! See if any of them extend from our our polymorphic types...
        foreach (INamedTypeSymbol type in _polymorphicTypesToLookForImplementation)
        {
            foreach (INamedTypeSymbol s in FetchImplementationsOf(type, serializableTypesFromParent))
            {
                MetadataType m = new() { Type = s, QualifiedName = s.FullyQualifiedName() };
                TrackPolymorphicType(type, m);
            }
        }
    }

    private void TrackRootSerializables(
        MurderTypeSymbols symbols)
    {
        TrackMetadataAndPrivateMembers(symbols, symbols.GameAssetClass);
        TrackMetadataAndPrivateMembers(symbols, symbols.ComponentInterface);
        TrackMetadataAndPrivateMembers(symbols, symbols.MessageInterface);
        TrackMetadataAndPrivateMembers(symbols, symbols.StateMachineClass);
        TrackMetadataAndPrivateMembers(symbols, symbols.StateMachineComponentInterface);
        TrackMetadataAndPrivateMembers(symbols, symbols.InteractionInterface);
        TrackMetadataAndPrivateMembers(symbols, symbols.InteractiveComponentInterface);
    }

    private void Populate(
        MurderTypeSymbols symbols,
        IEnumerable<INamedTypeSymbol> potentialStructs,
        IEnumerable<INamedTypeSymbol> potentialClasses)
    {
        var components = FetchComponents(symbols, potentialStructs);
        foreach (var component in components)
        {
            if (!IsSerializableType(symbols, component))
            {
                continue;
            }

            MetadataType m = new() { Type = component, QualifiedName = component.FullyQualifiedName() };
            TrackMetadataAndPrivateMembers(symbols, m);

            TrackPolymorphicType(symbols.ComponentInterface, m);
        }

        var messages = FetchMessages(symbols, potentialStructs);
        foreach (var message in messages)
        {
            if (!IsSerializableType(symbols, message))
            {
                continue;
            }

            MetadataType m = new() { Type = message, QualifiedName = message.FullyQualifiedName() };
            TrackMetadataAndPrivateMembers(symbols, m);

            TrackPolymorphicType(symbols.MessageInterface, m);
        }

        var stateMachines = FetchStateMachines(symbols, potentialClasses);
        foreach (var stateMachine in stateMachines)
        {
            if (!IsSerializableType(symbols, stateMachine))
            {
                continue;
            }

            if (stateMachine.IsAbstract)
            {
                TrackMetadataAndPrivateMembers(symbols, stateMachine);
            }
            else
            {
                string name = stateMachine.FullyQualifiedName();

                MetadataType m = new()
                {
                    Type = stateMachine,
                    QualifiedName = $"Bang.StateMachines.StateMachineComponent<{name}>"
                };

                TrackMetadataAndPrivateMembers(symbols, m);
                SerializableTypes.Add(name); // also add a metadata reference to the inner type of the state machine.

                TrackPolymorphicType(symbols.ComponentInterface, m);
                TrackPolymorphicType(symbols.StateMachineComponentInterface, m);
            }
        }

        var interactions = FetchInteractions(symbols, potentialStructs);
        foreach (var interaction in interactions)
        {
            if (!IsSerializableType(symbols, interaction))
            {
                continue;
            }

            if (interaction.IsAbstract)
            {
                TrackMetadataAndPrivateMembers(symbols, interaction);
            }
            else
            {
                string name = interaction.FullyQualifiedName();

                MetadataType m = new()
                {
                    Type = interaction,
                    QualifiedName = $"Bang.Interactions.InteractiveComponent<{name}>"
                };

                TrackMetadataAndPrivateMembers(symbols, m);
                SerializableTypes.Add(name); // also add a metadata reference to the inner type of the interaction.

                TrackPolymorphicType(symbols.ComponentInterface, m);
                TrackPolymorphicType(symbols.InteractiveComponentInterface, m);
            }
        }

        var assets = FetchGameAssets(symbols, potentialClasses);
        foreach (var asset in assets)
        {
            MetadataType m = new() { Type = asset, QualifiedName = asset.FullyQualifiedName() };

            TrackMetadataAndPrivateMembers(symbols, m);
            TrackPolymorphicType(symbols.GameAssetClass, m);
        }

        var otherTypes = FetchOtherSerializables(potentialClasses);
        foreach (var t in otherTypes)
        {
            TrackMetadataAndPrivateMembers(symbols, t);

            // Also track any of its derived types...
            _polymorphicTypesToLookForImplementation.Add(t);
        }

        IEnumerable<INamedTypeSymbol> allClassesAndStructs = [.. potentialClasses, .. potentialStructs];

        // Now, looks over all the referenced polymorphic types that need to be serialized as such.
        foreach (INamedTypeSymbol type in _polymorphicTypesToLookForImplementation)
        {
            SerializableTypes.Add(type.FullyQualifiedName()); // make sure the type itself has been added.

            foreach (INamedTypeSymbol s in FetchImplementationsOf(type, allClassesAndStructs))
            {
                MetadataType m = new() { Type = s, QualifiedName = s.FullyQualifiedName() };

                TrackMetadataAndPrivateMembers(symbols, m);
                TrackPolymorphicType(type, m);
            }
        }
    }

    private INamedTypeSymbol? FetchParentContext(MurderTypeSymbols symbols)
    {
        if (Mode is ScanMode.GenerateContextOnly)
        {
            return null;
        }

        return _referencedAssemblyTypeFetcher
            .FindFirstClassImplementationInParentAssemblyOf(symbols.SerializerContextInterface);
    }

    private IEnumerable<INamedTypeSymbol> FetchComponents(
        MurderTypeSymbols symbols,
        IEnumerable<INamedTypeSymbol> allValueTypesToBeCompiled) => 
        allValueTypesToBeCompiled
            .Where(t => !t.IsGenericType && t.ImplementsInterface(symbols.ComponentInterface))
            .OrderBy(c => c.Name);

    private IEnumerable<INamedTypeSymbol> FetchMessages(
        MurderTypeSymbols symbols,
        IEnumerable<INamedTypeSymbol> allValueTypesToBeCompiled) =>
        allValueTypesToBeCompiled
            .Where(t => !t.IsGenericType && t.ImplementsInterface(symbols.MessageInterface))
            .OrderBy(x => x.Name);

    private IEnumerable<INamedTypeSymbol> FetchInteractions(
        MurderTypeSymbols symbols,
        IEnumerable<INamedTypeSymbol> allValueTypesToBeCompiled) =>
        allValueTypesToBeCompiled
            .Where(t => !t.IsGenericType && t.ImplementsInterface(symbols.InteractionInterface))
            .OrderBy(i => i.Name);

    private IEnumerable<INamedTypeSymbol> FetchStateMachines(
        MurderTypeSymbols symbols,
        IEnumerable<INamedTypeSymbol> potentialStateMachines) =>
        potentialStateMachines
            .Where(t => t.IsSubtypeOf(symbols.StateMachineClass))
            .OrderBy(x => x.Name);

    private IEnumerable<INamedTypeSymbol> FetchGameAssets(
        MurderTypeSymbols symbols,
        IEnumerable<INamedTypeSymbol> potentialClasses) =>
        potentialClasses
            .Where(t => t.IsSubtypeOf(symbols.GameAssetClass))
            .OrderBy(x => x.Name);

    private IEnumerable<INamedTypeSymbol> FetchOtherSerializables(
        IEnumerable<INamedTypeSymbol> types) =>
        types
            .Where(t => t.IsSerializable)
            .OrderBy(i => i.Name);

    private IEnumerable<INamedTypeSymbol> FetchImplementationsOf(
        INamedTypeSymbol abstractType,
        IEnumerable<INamedTypeSymbol> types)
    {
        foreach (INamedTypeSymbol c in types)
        {
            if (c.ImplementsInterface(abstractType) || c.IsSubtypeOf(abstractType))
            {
                yield return c;
            }
        }
    }

    private void TrackMetadataAndPrivateMembers(MurderTypeSymbols symbols, ITypeSymbol t)
    {
        MetadataType metadata = new() { Type = t, QualifiedName = t.FullyQualifiedName() };
        if (!SerializableTypes.Add(metadata.QualifiedName))
        {
            // already tracked, bye.
            return;
        }

        MaybeLookForPrivateFields(symbols, t);
    }

    private void TrackMetadataAndPrivateMembers(MurderTypeSymbols symbols, MetadataType metadata)
    {
        if (!SerializableTypes.Add(metadata.QualifiedName))
        {
            // already tracked, bye.
            return;
        }

        MaybeLookForPrivateFields(symbols, metadata.Type);
    }

    private void MaybeLookForPrivateFields(MurderTypeSymbols symbols, ITypeSymbol t)
    {
        if (t.ContainingAssembly is null)
        {
            return;
        }

        if (!t.ContainingAssembly.Equals(_compilation.Assembly, SymbolEqualityComparer.Default))
        {
            return;
        }

        if (!_typesThatWereScannedForPrivateFields.Add(t))
        {
            return;
        }

        // Either this is root or matches the assembly of the parent, so we are okay checking it out.
        // Manually track private fields, because System.Text.Json won't do it for us.
        LookForPrivateCandidateFields(symbols, t);
    }

    /// <summary>
    /// Since private fields are not really picked up by the json source generators (which we do by reflection!), 
    /// we need to manually include them.
    /// </summary>
    private void LookForPrivateCandidateFields(MurderTypeSymbols murderSymbols, ITypeSymbol symbol)
    {
        foreach (ISymbol member in symbol.GetMembers())
        {
            if (member.Kind != SymbolKind.Field && member.Kind != SymbolKind.Property)
            {
                continue;
            }

            bool isSerializable = IsSerializableMember(murderSymbols, member);
            if (!isSerializable)
            {
                // not interesting to us.
                continue;
            }

            ITypeSymbol? memberType = member is IFieldSymbol field ? field.Type : member is IPropertySymbol property ? property.Type : null;
            if (memberType is null || _typesThatWereScannedForPrivateFields.Contains(memberType))
            {
                continue;
            }

            if (member.DeclaredAccessibility != Accessibility.Public)
            {
                TrackMetadataAndPrivateMembers(murderSymbols, memberType);
            }
            else
            {
                // Even though we are not explicitly tracking it, we will need to recursively check for its private members.
                MaybeLookForPrivateFields(murderSymbols, memberType);
            }

            if (IsPolymorphicCandidate(murderSymbols, memberType))
            {
                _polymorphicTypesToLookForImplementation.Add(memberType);
            }

            if (memberType is INamedTypeSymbol memberNamedType && memberNamedType.IsGenericType)
            {
                if (Mode is ScanMode.GenerateOptions &&
                    memberNamedType.ConstructedFrom.Equals(murderSymbols.ComplexDictionaryClass, SymbolEqualityComparer.Default))
                {
                    ComplexDictionaryArguments args = new() { Key = memberNamedType.TypeArguments[0], Value = memberNamedType.TypeArguments[1] };
                    ComplexDictionaries.Add(args);
                }

                foreach (INamedTypeSymbol a in memberNamedType.TypeArguments)
                {
                    if (IsPolymorphicCandidate(murderSymbols, a))
                    {
                        _polymorphicTypesToLookForImplementation.Add(a);
                    }
                }
            }
        }
    }

    /// <summary>
    /// For a given field with type <paramref name="s"/>, this will check whether this is a valid
    /// polymorphism candidate.
    /// Ideally, we want custom converters for any type that inherits from an interface.
    /// </summary>
    private bool IsPolymorphicCandidate(MurderTypeSymbols murderSymbols, ITypeSymbol s)
    {
        if (!s.IsAbstract)
        {
            return false;
        } 
        
        if (s.ContainingNamespace.Name.StartsWith("System"))
        {
            return false;
        }

        if (s.Equals(murderSymbols.ComponentInterface, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (s.Equals(murderSymbols.MessageInterface, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (s.Equals(murderSymbols.StateMachineClass, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (s.Equals(murderSymbols.StateMachineComponentInterface, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (s.Equals(murderSymbols.InteractionInterface, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (s.Equals(murderSymbols.InteractiveComponentInterface, SymbolEqualityComparer.Default))
        {
            return false;
        }

        if (s.Equals(murderSymbols.GameAssetClass, SymbolEqualityComparer.Default))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Track that whenever a field specified by <paramref name="derivedFrom"/> is found, serialize it
    /// as <paramref name="type"/>.
    /// </summary>
    private bool TrackPolymorphicType(ITypeSymbol derivedFrom, MetadataType type)
    {
        if (Mode is ScanMode.GenerateContextOnly)
        {
            return false;
        }

        if (!PolymorphicTypes.TryGetValue(derivedFrom, out HashSet<MetadataType>? existingTypes))
        {
            existingTypes = [];

            PolymorphicTypes[derivedFrom] = existingTypes;
        }

        return existingTypes.Add(type);
    }

    /// <summary>
    /// This follows the rules expected by Murder on saving assets and world entities in order to check
    /// if a type is serializable or not.
    /// </summary>
    private static bool IsSerializableType(MurderTypeSymbols murderSymbols, INamedTypeSymbol type)
    {
        foreach (AttributeData attribute in type.GetAttributes())
        {
            if (attribute.AttributeClass is not INamedTypeSymbol s)
            {
                continue;
            }

            if (s.Equals(murderSymbols.PersistOnSaveAttribute, SymbolEqualityComparer.Default))
            {
                return true;
            }

            if (s.Equals(murderSymbols.DoNotPersistOnSaveAttribute, SymbolEqualityComparer.Default))
            {
                return false;
            }

            if (s.Equals(murderSymbols.DoNotPersistEntityOnSaveAttribute, SymbolEqualityComparer.Default))
            {
                return false;
            }

            if (s.Equals(murderSymbols.RuntimeOnlyAttribute, SymbolEqualityComparer.Default))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Returns whether a member (property, field) is serialiazable.
    /// </summary>
    private static bool IsSerializableMember(MurderTypeSymbols murderSymbols, ISymbol member)
    {
        if (member is IPropertySymbol property && property.SetMethod is null)
        {
            // we very explicitly ignore { get; } properties.
            return false;
        }

        foreach (AttributeData attribute in member.GetAttributes())
        {
            if (attribute.AttributeClass is not INamedTypeSymbol s)
            {
                continue;
            }

            if (s.Equals(murderSymbols.IgnoreFieldAttribute, SymbolEqualityComparer.Default))
            {
                return false;
            }

            if (s.Equals(murderSymbols.SerializeFieldAttribute, SymbolEqualityComparer.Default))
            {
                return true;
            }

            if (s.Equals(murderSymbols.ShowInEditorFieldAttribute, SymbolEqualityComparer.Default))
            {
                return true;
            }
        }

        if (member.DeclaredAccessibility != Accessibility.Public)
        {
            return false;
        }

        return true;
    }

    private INamedTypeSymbol GetTypeSymbol(ClassDeclarationSyntax classDeclarationSyntax)
    {
        var semanticModel = _compilation.GetSemanticModel(classDeclarationSyntax.SyntaxTree);
        return (INamedTypeSymbol)semanticModel.GetDeclaredSymbol(classDeclarationSyntax)!;
    }

    private INamedTypeSymbol? ValueTypeFromTypeDeclarationSyntax(
        TypeDeclarationSyntax typeDeclarationSyntax)
    {
        var semanticModel = _compilation.GetSemanticModel(typeDeclarationSyntax.SyntaxTree);
        if (semanticModel.GetDeclaredSymbol(typeDeclarationSyntax) is not INamedTypeSymbol potentialComponentTypeSymbol)
        {
            return null;
        }

        // Record *classes* cannot be components or messages.
        if (typeDeclarationSyntax is RecordDeclarationSyntax && !potentialComponentTypeSymbol.IsValueType)
        {
            return null;
        }

        return potentialComponentTypeSymbol;
    }
}