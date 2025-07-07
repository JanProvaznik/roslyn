// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Analyzer.Utilities.Extensions;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Shared.Extensions;

using DiagnosticIds = Roslyn.Diagnostics.Analyzers.RoslynDiagnosticIds;

namespace Microsoft.CodeAnalysis.BannedApiAnalyzers
{
    using static BannedApiAnalyzerResources;

    /// <summary>
    /// POC analyzer that bans APIs only when used within implementations of IAsyncTask.ExecuteAsync()
    /// </summary>
    public abstract class IAsyncTaskBannedAnalyzer<TSyntaxKind> : SymbolIsBannedAnalyzerBase<TSyntaxKind>
        where TSyntaxKind : struct
    {
        private const string IAsyncTaskInterfaceName = "Microsoft.Build.Framework.IAsyncTask";
        private const string ConfigFileName = "IAsyncTaskBannedApis.txt";

        public static readonly DiagnosticDescriptor IAsyncTaskSymbolIsBannedRule = new(
            id: "RS0036", // Using a new ID for the POC
            title: CreateLocalizableResourceString(nameof(SymbolIsBannedTitle)),
            messageFormat: "Symbol '{0}' is banned in IAsyncTask implementations{1}",
            category: "ApiDesign",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "This symbol is banned when used within types that implement IAsyncTask.",
            helpLinkUri: "https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/Microsoft.CodeAnalysis.BannedApiAnalyzers/BannedApiAnalyzers.Help.md",
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        public sealed override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(IAsyncTaskSymbolIsBannedRule);

        protected sealed override DiagnosticDescriptor SymbolIsBannedRule => IAsyncTaskSymbolIsBannedRule;

        protected sealed override Dictionary<(string ContainerName, string SymbolName), ImmutableArray<BanFileEntry>>? ReadBannedApis(
            CompilationStartAnalysisContext compilationContext)
        {
            var compilation = compilationContext.Compilation;

            var query =
                from additionalFile in compilationContext.Options.AdditionalFiles
                let fileName = Path.GetFileName(additionalFile.Path)
                where fileName != null && fileName.Equals(ConfigFileName, StringComparison.OrdinalIgnoreCase)
                orderby additionalFile.Path
                let sourceText = additionalFile.GetText(compilationContext.CancellationToken)
                where sourceText != null
                from line in sourceText.Lines
                let text = line.ToString()
                let commentIndex = text.IndexOf("//", StringComparison.Ordinal)
                let textWithoutComment = commentIndex == -1 ? text : text[..commentIndex]
                where !string.IsNullOrWhiteSpace(textWithoutComment)
                let trimmedTextWithoutComment = textWithoutComment.TrimEnd()
                let span = commentIndex == -1 ? line.Span : new Text.TextSpan(line.Span.Start, trimmedTextWithoutComment.Length)
                let entry = new BanFileEntry(compilation, trimmedTextWithoutComment, span, sourceText, additionalFile.Path)
                where !string.IsNullOrWhiteSpace(entry.DeclarationId)
                select entry;

            var entries = query.ToList();

            if (entries.Count == 0)
                return null;

            var result = new Dictionary<(string ContainerName, string SymbolName), List<BanFileEntry>>();

            foreach (var entry in entries)
            {
                var parsed = DocumentationCommentIdParser.ParseDeclaredSymbolId(entry.DeclarationId);
                if (parsed is null)
                    continue;

                if (!result.TryGetValue(parsed.Value, out var existing))
                {
                    existing = [];
                    result.Add(parsed.Value, existing);
                }

                existing.Add(entry);
            }

            return result.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToImmutableArray());
        }

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private void OnCompilationStart(CompilationStartAnalysisContext compilationContext)
        {
            var bannedApis = ReadBannedApis(compilationContext);
            if (bannedApis == null || bannedApis.Count == 0)
                return;

            // Register operation analysis with method context checking
            compilationContext.RegisterOperationAction(
                context => AnalyzeOperationInContext(context, bannedApis),
                OperationKind.ObjectCreation,
                OperationKind.Invocation,
                OperationKind.EventReference,
                OperationKind.FieldReference,
                OperationKind.MethodReference,
                OperationKind.PropertyReference,
                OperationKind.ArrayCreation,
                OperationKind.AddressOf,
                OperationKind.Conversion,
                OperationKind.UnaryOperator,
                OperationKind.BinaryOperator,
                OperationKind.Increment,
                OperationKind.Decrement,
                OperationKind.TypeOf);
        }

        private void AnalyzeOperationInContext(
            OperationAnalysisContext context,
            Dictionary<(string ContainerName, string SymbolName), ImmutableArray<BanFileEntry>> bannedApis)
        {
            // Check if we're in a class that implements IAsyncTask
            var containingType = GetContainingType(context.Operation);
            if (containingType == null || !IsIAsyncTaskImplementation(containingType))
                return;

            // Now analyze the operation as usual
            context.CancellationToken.ThrowIfCancellationRequested();
            switch (context.Operation)
            {
                case IObjectCreationOperation objectCreation:
                    if (objectCreation.Constructor != null)
                        VerifySymbol(context.ReportDiagnostic, objectCreation.Constructor, context.Operation.Syntax, bannedApis);
                    VerifyType(context.ReportDiagnostic, objectCreation.Type, context.Operation.Syntax, bannedApis);
                    break;

                case IInvocationOperation invocation:
                    VerifySymbol(context.ReportDiagnostic, invocation.TargetMethod, context.Operation.Syntax, bannedApis);
                    VerifyType(context.ReportDiagnostic, invocation.TargetMethod.ContainingType, context.Operation.Syntax, bannedApis);
                    break;

                case IMemberReferenceOperation memberReference:
                    VerifySymbol(context.ReportDiagnostic, memberReference.Member, context.Operation.Syntax, bannedApis);
                    VerifyType(context.ReportDiagnostic, memberReference.Member.ContainingType, context.Operation.Syntax, bannedApis);
                    break;

                case IArrayCreationOperation arrayCreation:
                    VerifyType(context.ReportDiagnostic, arrayCreation.Type, context.Operation.Syntax, bannedApis);
                    break;

                case IAddressOfOperation addressOf:
                    VerifyType(context.ReportDiagnostic, addressOf.Type, context.Operation.Syntax, bannedApis);
                    break;

                case IConversionOperation conversion:
                    if (conversion.OperatorMethod != null)
                    {
                        VerifySymbol(context.ReportDiagnostic, conversion.OperatorMethod, context.Operation.Syntax, bannedApis);
                        VerifyType(context.ReportDiagnostic, conversion.OperatorMethod.ContainingType, context.Operation.Syntax, bannedApis);
                    }
                    break;

                case IUnaryOperation unary:
                    if (unary.OperatorMethod != null)
                    {
                        VerifySymbol(context.ReportDiagnostic, unary.OperatorMethod, context.Operation.Syntax, bannedApis);
                        VerifyType(context.ReportDiagnostic, unary.OperatorMethod.ContainingType, context.Operation.Syntax, bannedApis);
                    }
                    break;

                case IBinaryOperation binary:
                    if (binary.OperatorMethod != null)
                    {
                        VerifySymbol(context.ReportDiagnostic, binary.OperatorMethod, context.Operation.Syntax, bannedApis);
                        VerifyType(context.ReportDiagnostic, binary.OperatorMethod.ContainingType, context.Operation.Syntax, bannedApis);
                    }
                    break;

                case IIncrementOrDecrementOperation incrementOrDecrement:
                    if (incrementOrDecrement.OperatorMethod != null)
                    {
                        VerifySymbol(context.ReportDiagnostic, incrementOrDecrement.OperatorMethod, context.Operation.Syntax, bannedApis);
                        VerifyType(context.ReportDiagnostic, incrementOrDecrement.OperatorMethod.ContainingType, context.Operation.Syntax, bannedApis);
                    }
                    break;

                case ITypeOfOperation typeOfOperation:
                    VerifyType(context.ReportDiagnostic, typeOfOperation.TypeOperand, context.Operation.Syntax, bannedApis);
                    break;
            }
        }

        private INamedTypeSymbol? GetContainingType(IOperation operation)
        {
            // Walk up the operation tree to find the containing type
            var current = operation;
            while (current != null)
            {
                if (current.SemanticModel != null)
                {
                    // Look for the containing type symbol
                    var typeDeclaration = current.Syntax.Ancestors().FirstOrDefault(IsTypeDeclaration);
                    if (typeDeclaration != null)
                    {
                        var symbol = current.SemanticModel.GetDeclaredSymbol(typeDeclaration);
                        if (symbol is INamedTypeSymbol typeSymbol)
                            return typeSymbol;
                    }
                }
                current = current.Parent;
            }
            return null;
        }

        protected abstract bool IsTypeDeclaration(SyntaxNode node);

        private bool IsIAsyncTaskImplementation(INamedTypeSymbol typeSymbol)
        {
            // Check if the type implements IAsyncTask interface
            return typeSymbol.AllInterfaces.Any(i => i.ToDisplayString() == IAsyncTaskInterfaceName);
        }

        private void VerifySymbol(
            Action<Diagnostic> reportDiagnostic,
            ISymbol symbol,
            SyntaxNode syntaxNode,
            Dictionary<(string ContainerName, string SymbolName), ImmutableArray<BanFileEntry>> bannedApis)
        {
            foreach (var currentSymbol in GetSymbolAndOverriddenSymbols(symbol))
            {
                if (IsBannedSymbol(currentSymbol, bannedApis, out var entry))
                {
                    var message = entry?.Message;
                    reportDiagnostic(
                        syntaxNode.CreateDiagnostic(
                            SymbolIsBannedRule,
                            currentSymbol.ToDisplayString(SymbolDisplayFormat),
                            string.IsNullOrWhiteSpace(message) ? "" : ": " + message));
                    return;
                }
            }
        }

        private void VerifyType(
            Action<Diagnostic> reportDiagnostic,
            ITypeSymbol? type,
            SyntaxNode syntaxNode,
            Dictionary<(string ContainerName, string SymbolName), ImmutableArray<BanFileEntry>> bannedApis)
        {
            while (type != null)
            {
                if (IsBannedSymbol(type, bannedApis, out var entry))
                {
                    var message = entry?.Message;
                    reportDiagnostic(
                        syntaxNode.CreateDiagnostic(
                            SymbolIsBannedRule,
                            type.ToDisplayString(SymbolDisplayFormat),
                            string.IsNullOrWhiteSpace(message) ? "" : ": " + message));
                    return;
                }

                type = type.ContainingType;
            }
        }

        private static bool IsBannedSymbol(
            ISymbol? symbol,
            Dictionary<(string ContainerName, string SymbolName), ImmutableArray<BanFileEntry>> bannedApis,
            out BanFileEntry? entry)
        {
            entry = null;
            
            if (symbol is not { ContainingSymbol.Name: string parentName })
                return false;

            if (!bannedApis.TryGetValue((parentName, symbol.Name), out var entries))
                return false;

            foreach (var bannedFileEntry in entries)
            {
                foreach (var bannedSymbol in bannedFileEntry.Symbols)
                {
                    if (SymbolEqualityComparer.Default.Equals(symbol, bannedSymbol))
                    {
                        entry = bannedFileEntry;
                        return true;
                    }
                }
            }

            return false;
        }

        private static IEnumerable<ISymbol> GetSymbolAndOverriddenSymbols(ISymbol symbol)
        {
            ISymbol? currentSymbol = symbol.OriginalDefinition;

            while (currentSymbol != null)
            {
                yield return currentSymbol;

                currentSymbol = currentSymbol.IsOverride
                    ? currentSymbol.GetOverriddenMember()?.OriginalDefinition
                    : null;
            }
        }
    }
}
