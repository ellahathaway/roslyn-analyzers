﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using Analyzer.Utilities;
using Analyzer.Utilities.Extensions;
using Analyzer.Utilities.PooledObjects;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.ReleaseTracking;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Analyzers.MetaAnalyzers
{
    using static CodeAnalysisDiagnosticsResources;
    using PooledLocalizabeStringsConcurrentDictionary = PooledConcurrentDictionary<INamedTypeSymbol, PooledConcurrentSet<(IFieldSymbol field, IArgumentOperation argument)>>;
    using PooledResourcesDataValueConcurrentDictionary = PooledConcurrentDictionary<string, ImmutableDictionary<string, (string value, Location location)>>;
    using PooledFieldToResourceNameAndFileNameConcurrentDictionary = PooledConcurrentDictionary<IFieldSymbol, (string nameOfResource, string resourceFileName)>;

    [DiagnosticAnalyzer(LanguageNames.CSharp, LanguageNames.VisualBasic)]
    public sealed partial class DiagnosticDescriptorCreationAnalyzer : DiagnosticAnalyzer
    {
        private const string HelpLinkUriParameterName = "helpLinkUri";
        private const string CategoryParameterName = "category";
        private const string DiagnosticIdParameterName = "id";
        private const string CustomTagsParameterName = "customTags";
        private const string IsEnabledByDefaultParameterName = "isEnabledByDefault";
        private const string DefaultSeverityParameterName = "defaultSeverity";
        private const string RuleLevelParameterName = "ruleLevel";

        internal const string DefineDescriptorArgumentCorrectlyFixValue = nameof(DefineDescriptorArgumentCorrectlyFixValue);
        private const string DefineDescriptorArgumentCorrectlyFixAdditionalDocumentLocationInfo = nameof(DefineDescriptorArgumentCorrectlyFixAdditionalDocumentLocationInfo);
        private const string AdditionalDocumentLocationInfoSeparator = ";;";

        private static readonly ImmutableHashSet<string> CADiagnosticIdAllowedAssemblies = ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Microsoft.CodeAnalysis.VersionCheckAnalyzer",
            "Microsoft.CodeAnalysis.NetAnalyzers",
            "Microsoft.CodeAnalysis.CSharp.NetAnalyzers",
            "Microsoft.CodeAnalysis.VisualBasic.NetAnalyzers",
            "Microsoft.CodeQuality.Analyzers",
            "Microsoft.CodeQuality.CSharp.Analyzers",
            "Microsoft.CodeQuality.VisualBasic.Analyzers",
            "Microsoft.NetCore.Analyzers",
            "Microsoft.NetCore.CSharp.Analyzers",
            "Microsoft.NetCore.VisualBasic.Analyzers",
            "Microsoft.NetFramework.Analyzers",
            "Microsoft.NetFramework.CSharp.Analyzers",
            "Microsoft.NetFramework.VisualBasic.Analyzers",
            "Text.Analyzers",
            "Text.CSharp.Analyzers",
            "Text.VisualBasic.Analyzers");

        /// <summary>
        /// RS1007 (<inheritdoc cref="UseLocalizableStringsInDescriptorTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor UseLocalizableStringsInDescriptorRule = new(
            DiagnosticIds.UseLocalizableStringsInDescriptorRuleId,
            CreateLocalizableResourceString(nameof(UseLocalizableStringsInDescriptorTitle)),
            CreateLocalizableResourceString(nameof(UseLocalizableStringsInDescriptorMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisLocalization,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: false,
            description: CreateLocalizableResourceString(nameof(UseLocalizableStringsInDescriptorDescription)),
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1015 (<inheritdoc cref="ProvideHelpUriInDescriptorTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor ProvideHelpUriInDescriptorRule = new(
            DiagnosticIds.ProvideHelpUriInDescriptorRuleId,
            CreateLocalizableResourceString(nameof(ProvideHelpUriInDescriptorTitle)),
            CreateLocalizableResourceString(nameof(ProvideHelpUriInDescriptorMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDocumentation,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: false,
            description: CreateLocalizableResourceString(nameof(ProvideHelpUriInDescriptorDescription)),
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1017 (<inheritdoc cref="DiagnosticIdMustBeAConstantTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor DiagnosticIdMustBeAConstantRule = new(
            DiagnosticIds.DiagnosticIdMustBeAConstantRuleId,
            CreateLocalizableResourceString(nameof(DiagnosticIdMustBeAConstantTitle)),
            CreateLocalizableResourceString(nameof(DiagnosticIdMustBeAConstantMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDesign,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: CreateLocalizableResourceString(nameof(DiagnosticIdMustBeAConstantDescription)),
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1019 (<inheritdoc cref="UseUniqueDiagnosticIdTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor UseUniqueDiagnosticIdRule = new(
            DiagnosticIds.UseUniqueDiagnosticIdRuleId,
            CreateLocalizableResourceString(nameof(UseUniqueDiagnosticIdTitle)),
            CreateLocalizableResourceString(nameof(UseUniqueDiagnosticIdMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDesign,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: CreateLocalizableResourceString(nameof(UseUniqueDiagnosticIdDescription)),
            customTags: WellKnownDiagnosticTagsExtensions.CompilationEndAndTelemetry);

        /// <summary>
        /// RS1028 (<inheritdoc cref="ProvideCustomTagsInDescriptorTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor ProvideCustomTagsInDescriptorRule = new(
            DiagnosticIds.ProvideCustomTagsInDescriptorRuleId,
            CreateLocalizableResourceString(nameof(ProvideCustomTagsInDescriptorTitle)),
            CreateLocalizableResourceString(nameof(ProvideCustomTagsInDescriptorMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDocumentation,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: false,
            description: CreateLocalizableResourceString(nameof(ProvideCustomTagsInDescriptorDescription)),
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1029 (<inheritdoc cref="DoNotUseReservedDiagnosticIdTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor DoNotUseReservedDiagnosticIdRule = new(
            DiagnosticIds.DoNotUseReservedDiagnosticIdRuleId,
            CreateLocalizableResourceString(nameof(DoNotUseReservedDiagnosticIdTitle)),
            CreateLocalizableResourceString(nameof(DoNotUseReservedDiagnosticIdMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDesign,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: CreateLocalizableResourceString(nameof(DoNotUseReservedDiagnosticIdDescription)),
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1031 (<inheritdoc cref="DefineDiagnosticTitleCorrectlyTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor DefineDiagnosticTitleCorrectlyRule = new(
            DiagnosticIds.DefineDiagnosticTitleCorrectlyRuleId,
            CreateLocalizableResourceString(nameof(DefineDiagnosticTitleCorrectlyTitle)),
            CreateLocalizableResourceString(nameof(DefineDiagnosticTitleCorrectlyMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDesign,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1032 (<inheritdoc cref="DefineDiagnosticMessageCorrectlyTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor DefineDiagnosticMessageCorrectlyRule = new(
            DiagnosticIds.DefineDiagnosticMessageCorrectlyRuleId,
            CreateLocalizableResourceString(nameof(DefineDiagnosticMessageCorrectlyTitle)),
            CreateLocalizableResourceString(nameof(DefineDiagnosticMessageCorrectlyMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDesign,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        /// <summary>
        /// RS1033 (<inheritdoc cref="DefineDiagnosticDescriptionCorrectlyTitle"/>)
        /// </summary>
        public static readonly DiagnosticDescriptor DefineDiagnosticDescriptionCorrectlyRule = new(
            DiagnosticIds.DefineDiagnosticDescriptionCorrectlyRuleId,
            CreateLocalizableResourceString(nameof(DefineDiagnosticDescriptionCorrectlyTitle)),
            CreateLocalizableResourceString(nameof(DefineDiagnosticDescriptionCorrectlyMessage)),
            DiagnosticCategory.MicrosoftCodeAnalysisDesign,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            customTags: WellKnownDiagnosticTagsExtensions.Telemetry);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(
            UseLocalizableStringsInDescriptorRule,
            ProvideHelpUriInDescriptorRule,
            DiagnosticIdMustBeAConstantRule,
            DiagnosticIdMustBeInSpecifiedFormatRule,
            UseUniqueDiagnosticIdRule,
            UseCategoriesFromSpecifiedRangeRule,
            AnalyzerCategoryAndIdRangeFileInvalidRule,
            ProvideCustomTagsInDescriptorRule,
            DoNotUseReservedDiagnosticIdRule,
            DeclareDiagnosticIdInAnalyzerReleaseRule,
            UpdateDiagnosticIdInAnalyzerReleaseRule,
            RemoveUnshippedDeletedDiagnosticIdRule,
            RemoveShippedDeletedDiagnosticIdRule,
            UnexpectedAnalyzerDiagnosticForRemovedDiagnosticIdRule,
            RemoveDuplicateEntriesForAnalyzerReleaseRule,
            RemoveDuplicateEntriesBetweenAnalyzerReleasesRule,
            InvalidEntryInAnalyzerReleasesFileRule,
            InvalidHeaderInAnalyzerReleasesFileRule,
            InvalidUndetectedEntryInAnalyzerReleasesFileRule,
            InvalidRemovedOrChangedWithoutPriorNewEntryInAnalyzerReleasesFileRule,
            EnableAnalyzerReleaseTrackingRule,
            DefineDiagnosticTitleCorrectlyRule,
            DefineDiagnosticMessageCorrectlyRule,
            DefineDiagnosticDescriptionCorrectlyRule);

        public override void Initialize(AnalysisContext context)
        {
            context.EnableConcurrentExecution();
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);

            context.RegisterCompilationStartAction(compilationContext =>
            {
                if (!compilationContext.Compilation.TryGetOrCreateTypeByMetadataName(WellKnownTypeNames.MicrosoftCodeAnalysisDiagnosticDescriptor, out var diagnosticDescriptorType) ||
                    !compilationContext.Compilation.TryGetOrCreateTypeByMetadataName(WellKnownTypeNames.MicrosoftCodeAnalysisLocalizableString, out var localizableResourceType) ||
                    !compilationContext.Compilation.TryGetOrCreateTypeByMetadataName(WellKnownTypeNames.MicrosoftCodeAnalysisLocalizableResourceString, out var localizableResourceStringType))
                {
                    return;
                }

                // Try read the additional file containing the allowed categories, and corresponding ID ranges.
                var checkCategoryAndAllowedIds = TryGetCategoryAndAllowedIdsMap(
                    compilationContext.Options.AdditionalFiles,
                    compilationContext.CancellationToken,
                    out AdditionalText? diagnosticCategoryAndIdRangeText,
                    out ImmutableDictionary<string, ImmutableArray<(string? prefix, int start, int end)>>? categoryAndAllowedIdsMap,
                    out List<Diagnostic>? invalidFileDiagnostics);

                // Try read the additional files containing the shipped and unshipped analyzer releases.
                var isAnalyzerReleaseTracking = TryGetReleaseTrackingData(
                    compilationContext.Options.AdditionalFiles,
                    compilationContext.CancellationToken,
                    out var shippedData,
                    out var unshippedData,
                    out List<Diagnostic>? invalidReleaseFileEntryDiagnostics);

                PooledLocalizabeStringsConcurrentDictionary? localizableTitles = null;
                PooledLocalizabeStringsConcurrentDictionary? localizableMessages = null;
                PooledLocalizabeStringsConcurrentDictionary? localizableDescriptions = null;
                PooledResourcesDataValueConcurrentDictionary? resourcesDataValueMap = null;

                var analyzeResourceStrings = HasResxAdditionalFiles(compilationContext.Options);
                if (analyzeResourceStrings)
                {
                    localizableTitles = PooledLocalizabeStringsConcurrentDictionary.GetInstance();
                    localizableMessages = PooledLocalizabeStringsConcurrentDictionary.GetInstance();
                    localizableDescriptions = PooledLocalizabeStringsConcurrentDictionary.GetInstance();
                    resourcesDataValueMap = PooledResourcesDataValueConcurrentDictionary.GetInstance();
                }

                var idToAnalyzerMap = new ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentBag<Location>>>();
                var seenRuleIds = PooledConcurrentSet<string>.GetInstance();
                compilationContext.RegisterOperationAction(operationAnalysisContext =>
                {
                    var fieldInitializer = (IFieldInitializerOperation)operationAnalysisContext.Operation;
                    if (!TryGetDescriptorCreateMethodAndArguments(fieldInitializer, diagnosticDescriptorType, out var creationMethod, out var creationArguments))
                    {
                        return;
                    }

                    var containingType = operationAnalysisContext.ContainingSymbol.ContainingType;
                    AnalyzeTitle(operationAnalysisContext, creationArguments, fieldInitializer, containingType,
                        localizableTitles, resourcesDataValueMap, localizableResourceType, localizableResourceStringType);
                    AnalyzeMessage(operationAnalysisContext, creationArguments, containingType,
                        localizableMessages, resourcesDataValueMap, localizableResourceType, localizableResourceStringType);
                    AnalyzeDescription(operationAnalysisContext, creationArguments, containingType,
                        localizableDescriptions, resourcesDataValueMap, localizableResourceType, localizableResourceStringType);
                    AnalyzeHelpLinkUri(operationAnalysisContext, creationArguments, out var helpLink);
                    AnalyzeCustomTags(operationAnalysisContext, creationArguments);
                    var (isEnabledByDefault, defaultSeverity) = GetDefaultSeverityAndEnabledByDefault(operationAnalysisContext.Compilation, creationArguments);

                    if (!TryAnalyzeCategory(operationAnalysisContext, creationArguments, checkCategoryAndAllowedIds,
                            diagnosticCategoryAndIdRangeText, categoryAndAllowedIdsMap, out var category, out var allowedIdsInfoList))
                    {
                        allowedIdsInfoList = default;
                    }

                    var analyzerName = fieldInitializer.InitializedFields.First().ContainingType.Name;
                    AnalyzeRuleId(operationAnalysisContext, creationArguments,
                        isAnalyzerReleaseTracking, shippedData, unshippedData, seenRuleIds, diagnosticCategoryAndIdRangeText,
                        category, analyzerName, helpLink, isEnabledByDefault, defaultSeverity, allowedIdsInfoList, idToAnalyzerMap);

                }, OperationKind.FieldInitializer);

                if (analyzeResourceStrings)
                {
                    compilationContext.RegisterSymbolStartAction(context =>
                    {
                        var symbolToResourceMap = PooledFieldToResourceNameAndFileNameConcurrentDictionary.GetInstance(SymbolEqualityComparer.Default);
                        context.RegisterOperationAction(context =>
                        {
                            var fieldInitializer = (IFieldInitializerOperation)context.Operation;
                            if (TryGetLocalizableResourceStringCreation(fieldInitializer.Value, localizableResourceStringType,
                                    out var nameOfLocalizableResource, out var resourceFileName))
                            {
                                foreach (var field in fieldInitializer.InitializedFields)
                                {
                                    symbolToResourceMap.TryAdd(field, (nameOfLocalizableResource, resourceFileName));
                                }
                            }
                        }, OperationKind.FieldInitializer);

                        context.RegisterSymbolEndAction(context =>
                        {
                            RoslynDebug.Assert(localizableTitles != null);
                            RoslynDebug.Assert(localizableMessages != null);
                            RoslynDebug.Assert(localizableDescriptions != null);
                            RoslynDebug.Assert(resourcesDataValueMap != null);

                            var namedType = (INamedTypeSymbol)context.Symbol;

                            AnalyzeLocalizableStrings(localizableTitles, AnalyzeTitleCore, symbolToResourceMap, namedType,
                                resourcesDataValueMap, context.Options, context.ReportDiagnostic, context.CancellationToken);
                            AnalyzeLocalizableStrings(localizableMessages, AnalyzeMessageCore, symbolToResourceMap, namedType,
                                resourcesDataValueMap, context.Options, context.ReportDiagnostic, context.CancellationToken);
                            AnalyzeLocalizableStrings(localizableDescriptions, AnalyzeDescriptionCore, symbolToResourceMap, namedType,
                                resourcesDataValueMap, context.Options, context.ReportDiagnostic, context.CancellationToken);

                            symbolToResourceMap.Free(context.CancellationToken);
                        });
                    }, SymbolKind.NamedType);
                }

                compilationContext.RegisterCompilationEndAction(compilationEndContext =>
                {
                    // Report any invalid additional file diagnostics.
                    if (invalidFileDiagnostics != null)
                    {
                        foreach (var diagnostic in invalidFileDiagnostics)
                        {
                            compilationEndContext.ReportDiagnostic(diagnostic);
                        }
                    }

                    // Report diagnostics for duplicate diagnostic ID used across analyzers.
                    foreach (var kvp in idToAnalyzerMap)
                    {
                        var ruleId = kvp.Key;
                        var analyzerToDescriptorLocationsMap = kvp.Value;
                        if (analyzerToDescriptorLocationsMap.Count <= 1)
                        {
                            // ID used by a single analyzer.
                            continue;
                        }

                        ImmutableSortedSet<string> sortedAnalyzerNames = analyzerToDescriptorLocationsMap.Keys.ToImmutableSortedSet();
                        var skippedAnalyzerName = sortedAnalyzerNames[0];
                        foreach (var analyzerName in sortedAnalyzerNames.Skip(1))
                        {
                            var locations = analyzerToDescriptorLocationsMap[analyzerName];
                            foreach (var location in locations)
                            {
                                // Diagnostic Id '{0}' is already used by analyzer '{1}'. Please use a different diagnostic ID.
                                var diagnostic = Diagnostic.Create(UseUniqueDiagnosticIdRule, location, ruleId, skippedAnalyzerName);
                                compilationEndContext.ReportDiagnostic(diagnostic);
                            }
                        }
                    }

                    // Report analyzer release tracking invalid entry and compilation end diagnostics.
                    if (isAnalyzerReleaseTracking || invalidReleaseFileEntryDiagnostics != null)
                    {
                        RoslynDebug.Assert(shippedData != null);
                        RoslynDebug.Assert(unshippedData != null);

                        ReportAnalyzerReleaseTrackingDiagnostics(invalidReleaseFileEntryDiagnostics, shippedData, unshippedData, seenRuleIds, compilationEndContext);
                    }

                    seenRuleIds.Free(compilationEndContext.CancellationToken);
                    if (analyzeResourceStrings)
                    {
                        RoslynDebug.Assert(localizableTitles != null);
                        RoslynDebug.Assert(localizableMessages != null);
                        RoslynDebug.Assert(localizableDescriptions != null);
                        RoslynDebug.Assert(resourcesDataValueMap != null);

                        FreeLocalizableStringsMap(localizableTitles, compilationEndContext.CancellationToken);
                        FreeLocalizableStringsMap(localizableMessages, compilationEndContext.CancellationToken);
                        FreeLocalizableStringsMap(localizableDescriptions, compilationEndContext.CancellationToken);
                        resourcesDataValueMap.Free(compilationEndContext.CancellationToken);
                    }
                });
            });

            static void FreeLocalizableStringsMap(PooledLocalizabeStringsConcurrentDictionary localizableStrings, CancellationToken cancellationToken)
            {
                foreach (var builder in localizableStrings.Values)
                {
                    builder.Free(cancellationToken);
                }

                localizableStrings.Free(cancellationToken);
            }
        }

        private static bool TryGetDescriptorCreateMethodAndArguments(
            IFieldInitializerOperation fieldInitializer,
            INamedTypeSymbol diagnosticDescriptorType,
            [NotNullWhen(returnValue: true)] out IMethodSymbol? creationMethod,
            [NotNullWhen(returnValue: true)] out ImmutableArray<IArgumentOperation> creationArguments)
        {
            (creationMethod, creationArguments) = fieldInitializer.Value switch
            {
                IObjectCreationOperation objectCreation when IsDescriptorConstructor(objectCreation.Constructor)
                    => (objectCreation.Constructor, objectCreation.Arguments),
                IInvocationOperation invocation when IsCreateHelper(invocation.TargetMethod)
                    => (invocation.TargetMethod, invocation.Arguments),
                _ => default
            };

            return creationMethod != null;

            bool IsDescriptorConstructor(IMethodSymbol method)
                => method.ContainingType.Equals(diagnosticDescriptorType);

            // Heuristic to identify helper methods to create DiagnosticDescriptor:
            //  "A method invocation that returns 'DiagnosticDescriptor' and has a first string parameter named 'id'"
            bool IsCreateHelper(IMethodSymbol method)
                => method.ReturnType.Equals(diagnosticDescriptorType) &&
                    !method.Parameters.IsEmpty &&
                    method.Parameters[0].Name == DiagnosticIdParameterName &&
                    method.Parameters[0].Type.SpecialType == SpecialType.System_String;
        }

        private static bool TryGetLocalizableResourceStringCreation(
            IOperation operation,
            INamedTypeSymbol localizableResourceStringType,
            [NotNullWhen(returnValue: true)] out string? nameOfLocalizableResource,
            [NotNullWhen(returnValue: true)] out string? resourceFileName)
        {
            if (operation.WalkDownConversion() is IObjectCreationOperation objectCreation &&
                objectCreation.Constructor.ContainingType.Equals(localizableResourceStringType) &&
                objectCreation.Arguments.Length >= 3 &&
                objectCreation.Arguments.GetArgumentForParameterAtIndex(0) is { } firstParamArgument &&
                firstParamArgument.Parameter.Type.SpecialType == SpecialType.System_String &&
                firstParamArgument.Value.ConstantValue.HasValue &&
                firstParamArgument.Value.ConstantValue.Value is string nameOfResource &&
                objectCreation.Arguments.GetArgumentForParameterAtIndex(2) is { } thirdParamArgument &&
                thirdParamArgument.Value is ITypeOfOperation typeOfOperation &&
                typeOfOperation.TypeOperand is { } typeOfType)
            {
                nameOfLocalizableResource = nameOfResource;
                resourceFileName = typeOfType.Name;
                return true;
            }

            nameOfLocalizableResource = null;
            resourceFileName = null;
            return false;
        }

        private static void AnalyzeTitle(
            OperationAnalysisContext operationAnalysisContext,
            ImmutableArray<IArgumentOperation> creationArguments,
            IFieldInitializerOperation creation,
            INamedTypeSymbol containingType,
            PooledLocalizabeStringsConcurrentDictionary? localizableTitles,
            PooledResourcesDataValueConcurrentDictionary? resourceDataValueMap,
            INamedTypeSymbol localizableStringType,
            INamedTypeSymbol localizableResourceStringType)
        {
            IArgumentOperation titleArgument = creationArguments.FirstOrDefault(a => a.Parameter.Name.Equals("title", StringComparison.OrdinalIgnoreCase));
            if (titleArgument != null)
            {
                if (titleArgument.Parameter.Type.SpecialType == SpecialType.System_String)
                {
                    operationAnalysisContext.ReportDiagnostic(creation.Value.CreateDiagnostic(UseLocalizableStringsInDescriptorRule, WellKnownTypeNames.MicrosoftCodeAnalysisLocalizableString));
                }

                AnalyzeDescriptorArgument(operationAnalysisContext, titleArgument,
                    AnalyzeTitleCore, containingType, localizableTitles, resourceDataValueMap,
                    localizableStringType, localizableResourceStringType);
            }
        }

        private static void AnalyzeTitleCore(string title, IArgumentOperation argumentOperation, Location fixLocation, Action<Diagnostic> reportDiagnostic)
        {
            var hasLeadingOrTrailingWhitespaces = HasLeadingOrTrailingWhitespaces(title);
            if (hasLeadingOrTrailingWhitespaces)
            {
                title = RemoveLeadingAndTrailingWhitespaces(title);
            }

            var isMultiSentences = IsMultiSentences(title);
            var endsWithPeriod = EndsWithPeriod(title);
            var containsLineReturn = ContainsLineReturn(title);

            if (isMultiSentences || endsWithPeriod || containsLineReturn || hasLeadingOrTrailingWhitespaces)
            {
                // Leading and trailing spaces were already fixed
                var fixedTitle = endsWithPeriod ? RemoveTrailingPeriod(title) : title;
                fixedTitle = isMultiSentences ? FixMultiSentences(fixedTitle) : fixedTitle;
                fixedTitle = containsLineReturn ? FixLineReturns(fixedTitle, allowMultisentences: false) : fixedTitle;

                ReportDefineDiagnosticArgumentCorrectlyDiagnostic(DefineDiagnosticTitleCorrectlyRule,
                    argumentOperation, fixedTitle, fixLocation, reportDiagnostic);
            }
        }

        private static void ReportDefineDiagnosticArgumentCorrectlyDiagnostic(
            DiagnosticDescriptor descriptor,
            IArgumentOperation argumentOperation,
            string fixValue,
            Location fixLocation,
            Action<Diagnostic> reportDiagnostic)
        {
            // Additional location in an additional document does not seem to be preserved
            // from analyzer to code fix due to a Roslyn bug: https://github.com/dotnet/roslyn/issues/46377
            // We workaround this bug by passing additional document file path and location span as strings.

            var additionalLocations = ImmutableArray<Location>.Empty;
            var properties = ImmutableDictionary<string, string?>.Empty.Add(DefineDescriptorArgumentCorrectlyFixValue, fixValue);
            if (fixLocation.IsInSource)
            {
                additionalLocations = additionalLocations.Add(fixLocation);
            }
            else
            {
                var span = fixLocation.SourceSpan;
                var locationInfo = $"{span.Start}{AdditionalDocumentLocationInfoSeparator}{span.Length}{AdditionalDocumentLocationInfoSeparator}{fixLocation.GetLineSpan().Path}";
                properties = properties.Add(DefineDescriptorArgumentCorrectlyFixAdditionalDocumentLocationInfo, locationInfo);
            }

            reportDiagnostic(argumentOperation.CreateDiagnostic(descriptor, additionalLocations, properties));
        }

        internal static bool TryGetAdditionalDocumentLocationInfo(Diagnostic diagnostic,
            [NotNullWhen(returnValue: true)] out string? filePath,
            [NotNullWhen(returnValue: true)] out TextSpan? fileSpan)
        {
            Debug.Assert(diagnostic.Id is DiagnosticIds.DefineDiagnosticTitleCorrectlyRuleId or
                DiagnosticIds.DefineDiagnosticMessageCorrectlyRuleId or
                DiagnosticIds.DefineDiagnosticDescriptionCorrectlyRuleId);

            filePath = null;
            fileSpan = null;
            if (!diagnostic.Properties.TryGetValue(DefineDescriptorArgumentCorrectlyFixAdditionalDocumentLocationInfo, out var locationInfo))
            {
                return false;
            }

            var parts = locationInfo.Split(new[] { AdditionalDocumentLocationInfoSeparator }, StringSplitOptions.None);
            if (parts.Length != 3 ||
                !int.TryParse(parts[0], out var spanSpart) ||
                !int.TryParse(parts[1], out var spanLength))
            {
                return false;
            }

            fileSpan = new TextSpan(spanSpart, spanLength);
            filePath = parts[2];
            return !string.IsNullOrEmpty(filePath);
        }

        private static void AnalyzeMessage(
            OperationAnalysisContext operationAnalysisContext,
            ImmutableArray<IArgumentOperation> creationArguments,
            INamedTypeSymbol containingType,
            PooledLocalizabeStringsConcurrentDictionary? localizableMessages,
            PooledResourcesDataValueConcurrentDictionary? resourceDataValueMap,
            INamedTypeSymbol localizableStringType,
            INamedTypeSymbol localizableResourceStringType)
        {
            var messageArgument = creationArguments.FirstOrDefault(a => a.Parameter.Name.Equals("messageFormat", StringComparison.OrdinalIgnoreCase));
            if (messageArgument != null)
            {
                AnalyzeDescriptorArgument(operationAnalysisContext, messageArgument,
                    AnalyzeMessageCore, containingType, localizableMessages, resourceDataValueMap,
                    localizableStringType, localizableResourceStringType);
            }
        }

        private static void AnalyzeMessageCore(string message, IArgumentOperation argumentOperation, Location fixLocation, Action<Diagnostic> reportDiagnostic)
        {
            var hasLeadingOrTrailingWhitespaces = HasLeadingOrTrailingWhitespaces(message);
            if (hasLeadingOrTrailingWhitespaces)
            {
                message = RemoveLeadingAndTrailingWhitespaces(message);
            }

            var isMultiSentences = IsMultiSentences(message);
            var endsWithPeriod = EndsWithPeriod(message);
            var containsLineReturn = ContainsLineReturn(message);

            if (isMultiSentences ^ endsWithPeriod || containsLineReturn || hasLeadingOrTrailingWhitespaces)
            {
                // Leading and trailing spaces were already fixed
                var fixedMessage = containsLineReturn ? FixLineReturns(message, allowMultisentences: true) : message;
                isMultiSentences = IsMultiSentences(fixedMessage);
                endsWithPeriod = EndsWithPeriod(fixedMessage);

                if (isMultiSentences ^ endsWithPeriod)
                {
                    fixedMessage = endsWithPeriod ? RemoveTrailingPeriod(fixedMessage) : fixedMessage + ".";
                }

                ReportDefineDiagnosticArgumentCorrectlyDiagnostic(DefineDiagnosticMessageCorrectlyRule,
                    argumentOperation, fixedMessage, fixLocation, reportDiagnostic);
            }
        }

        private static void AnalyzeDescription(
            OperationAnalysisContext operationAnalysisContext,
            ImmutableArray<IArgumentOperation> creationArguments,
            INamedTypeSymbol containingType,
            PooledLocalizabeStringsConcurrentDictionary? localizableDescriptions,
            PooledResourcesDataValueConcurrentDictionary? resourceDataValueMap,
            INamedTypeSymbol localizableStringType,
            INamedTypeSymbol localizableResourceStringType)
        {
            IArgumentOperation descriptionArgument = creationArguments.FirstOrDefault(a => a.Parameter.Name.Equals("description", StringComparison.OrdinalIgnoreCase));
            if (descriptionArgument != null)
            {
                AnalyzeDescriptorArgument(operationAnalysisContext, descriptionArgument,
                    AnalyzeDescriptionCore, containingType, localizableDescriptions, resourceDataValueMap,
                    localizableStringType, localizableResourceStringType);
            }
        }

        private static void AnalyzeDescriptionCore(string description, IArgumentOperation argumentOperation, Location fixLocation, Action<Diagnostic> reportDiagnostic)
        {
            var hasLeadingOrTrailingWhitespaces = HasLeadingOrTrailingWhitespaces(description);
            if (hasLeadingOrTrailingWhitespaces)
            {
                description = RemoveLeadingAndTrailingWhitespaces(description);
            }

            var endsWithPunctuation = EndsWithPunctuation(description);

            if (!endsWithPunctuation || hasLeadingOrTrailingWhitespaces)
            {
                var fixedDescription = !endsWithPunctuation ? description + "." : description;

                ReportDefineDiagnosticArgumentCorrectlyDiagnostic(DefineDiagnosticDescriptionCorrectlyRule,
                    argumentOperation, fixedDescription, fixLocation, reportDiagnostic);
            }
        }

        private static void AnalyzeDescriptorArgument(
            OperationAnalysisContext operationAnalysisContext,
            IArgumentOperation argument,
            Action<string, IArgumentOperation, Location, Action<Diagnostic>> analyzeStringValueCore,
            INamedTypeSymbol containingType,
            PooledLocalizabeStringsConcurrentDictionary? localizableStringsMap,
            PooledResourcesDataValueConcurrentDictionary? resourceDataValueMap,
            INamedTypeSymbol localizableStringType,
            INamedTypeSymbol localizableResourceStringType)
        {
            if (TryGetNonEmptyConstantStringValue(argument, out var argumentValue, out var argumentValueLocation))
            {
                analyzeStringValueCore(argumentValue, argument, argumentValueLocation, operationAnalysisContext.ReportDiagnostic);
            }
            else if (localizableStringsMap != null &&
                argument.Parameter.Type.Equals(localizableStringType))
            {
                RoslynDebug.Assert(resourceDataValueMap != null);

                if (TryGetLocalizableResourceStringCreation(argument.Value, localizableResourceStringType,
                        out var nameOfLocalizableResource, out var resourceFileName))
                {
                    AnalyzeLocalizableDescriptorArgument(analyzeStringValueCore, nameOfLocalizableResource, resourceFileName,
                        argument, resourceDataValueMap, operationAnalysisContext.Options,
                        operationAnalysisContext.ReportDiagnostic, operationAnalysisContext.CancellationToken);
                }
                else
                {
                    var value = argument.Value.WalkDownConversion();
                    if (value is IFieldReferenceOperation fieldReference &&
                        fieldReference.Field.Type.DerivesFrom(localizableStringType, baseTypesOnly: true))
                    {
                        var builder = localizableStringsMap.GetOrAdd(containingType, _ => PooledConcurrentSet<(IFieldSymbol, IArgumentOperation)>.GetInstance());
                        builder.Add((fieldReference.Field, argument));
                    }
                }
            }
        }

        private static void AnalyzeLocalizableDescriptorArgument(
            Action<string, IArgumentOperation, Location, Action<Diagnostic>> analyzeStringValueCore,
            string nameOfLocalizableResource,
            string resourceFileName,
            IArgumentOperation argument,
            PooledResourcesDataValueConcurrentDictionary resourceDataValueMap,
            AnalyzerOptions options,
            Action<Diagnostic> reportDiagnostic,
            CancellationToken cancellationToken)
        {
            var map = GetOrCreateResourceMap(options, resourceFileName, resourceDataValueMap, cancellationToken);
            if (map.TryGetValue(nameOfLocalizableResource, out var resourceStringTuple))
            {
                analyzeStringValueCore(resourceStringTuple.value, argument, resourceStringTuple.location, reportDiagnostic);
            }
        }

        private static void AnalyzeLocalizableStrings(
            PooledLocalizabeStringsConcurrentDictionary localizableStringsMap,
            Action<string, IArgumentOperation, Location, Action<Diagnostic>> analyzeLocalizableStringValueCore,
            PooledFieldToResourceNameAndFileNameConcurrentDictionary symbolToResourceMap,
            INamedTypeSymbol namedType,
            PooledResourcesDataValueConcurrentDictionary resourceDataValueMap,
            AnalyzerOptions options,
            Action<Diagnostic> reportDiagnostic,
            CancellationToken cancellationToken)
        {
            if (localizableStringsMap.TryRemove(namedType, out var localizableFieldsWithOriginalArguments))
            {
                foreach (var (field, argument) in localizableFieldsWithOriginalArguments)
                {
                    if (symbolToResourceMap.TryGetValue(field, out var resourceTuple))
                    {
                        AnalyzeLocalizableDescriptorArgument(analyzeLocalizableStringValueCore, resourceTuple.nameOfResource, resourceTuple.resourceFileName,
                            argument, resourceDataValueMap, options, reportDiagnostic, cancellationToken);
                    }
                }

                localizableFieldsWithOriginalArguments.Dispose();
            }
        }

        private static bool TryGetNonEmptyConstantStringValue(
            IArgumentOperation argumentOperation,
            [NotNullWhen(true)] out string? value,
            [NotNullWhen(true)] out Location? valueLocation)
        {
            value = null;
            valueLocation = null;

            IOperation valueOperation;
            var argumentValueOperation = argumentOperation.Value.WalkDownConversion();
            if (argumentValueOperation is ILiteralOperation literalOperation)
            {
                valueOperation = literalOperation;
            }
            else if (argumentValueOperation is IFieldReferenceOperation fieldReferenceOperation &&
                fieldReferenceOperation.Syntax.SyntaxTree == argumentValueOperation.Syntax.SyntaxTree &&
                fieldReferenceOperation.Field.DeclaringSyntaxReferences.Length == 1 &&
                fieldReferenceOperation.Field.DeclaringSyntaxReferences[0].GetSyntax() is { } fieldDeclaration &&
                GetFieldInitializer(fieldDeclaration, argumentValueOperation.SemanticModel) is { } fieldInitializer &&
                fieldInitializer.Value.WalkDownConversion() is ILiteralOperation fieldInitializerLiteral)
            {
                valueOperation = fieldInitializerLiteral;
            }
            else
            {
                valueOperation = argumentValueOperation;
            }

            if (!TryGetNonEmptyConstantStringValueCore(valueOperation, out var literalValue))
            {
                return false;
            }

            value = literalValue;
            valueLocation = valueOperation.Syntax.GetLocation();
            return true;

            static IFieldInitializerOperation? GetFieldInitializer(SyntaxNode fieldDeclaration, SemanticModel model)
            {
                if (fieldDeclaration.Language == LanguageNames.VisualBasic)
                {
                    // For VB, the field initializer is on the parent node.
                    fieldDeclaration = fieldDeclaration.Parent;
                }

                foreach (var node in fieldDeclaration.DescendantNodes())
                {
                    if (model.GetOperation(node) is IFieldInitializerOperation initializer)
                    {
                        return initializer;
                    }
                }

                return null;
            }
        }

        private static bool TryGetNonEmptyConstantStringValueCore(IOperation operation, [NotNullWhen(returnValue: true)] out string? literalValue)
        {
            if (operation.ConstantValue.HasValue &&
                operation.ConstantValue.Value is string value &&
                !string.IsNullOrEmpty(value))
            {
                literalValue = value;
                return true;
            }

            literalValue = null;
            return false;
        }

        // Assumes that a string is a multi-sentences if it contains a period followed by a whitespace ('. ').
        private const string MultiSentenceSeparator = ". ";

        private static bool IsMultiSentences(string s)
            => s.Contains(MultiSentenceSeparator);

        private static string FixMultiSentences(string s)
        {
            Debug.Assert(IsMultiSentences(s));
            var index = s.IndexOf(MultiSentenceSeparator, StringComparison.OrdinalIgnoreCase);
            return s.Substring(0, index);
        }

        private static bool EndsWithPeriod(string s)
            => s[^1] == '.';

        private static string RemoveTrailingPeriod(string s)
        {
            Debug.Assert(EndsWithPeriod(s));
            return s[0..^1];
        }

        private static bool ContainsLineReturn(string s)
            => s.Contains("\r") || s.Contains("\n");

        private static string FixLineReturns(string s, bool allowMultisentences)
        {
            Debug.Assert(ContainsLineReturn(s));

            var parts = s.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            if (!allowMultisentences)
            {
                return parts[0];
            }

            var builder = new StringBuilder();
            for (var i = 0; i < parts.Length; i++)
            {
                var part = parts[i];
                if (!EndsWithPeriod(part))
                {
                    part += ".";
                }

                if (part.TrimEnd().Length == part.Length &&
                    i < parts.Length - 1)
                {
                    part += " ";
                }

                builder.Append(part);
            }

            return builder.ToString();
        }

        private static bool EndsWithPunctuation(string s)
        {
            var lastChar = s[^1];

            return lastChar.Equals('.') || lastChar.Equals('!') || lastChar.Equals('?');
        }

        private static string RemoveTrailingPunctuation(string s)
        {
            Debug.Assert(EndsWithPunctuation(s));
            return s[0..^1];
        }

        private static bool HasLeadingOrTrailingWhitespaces(string s)
            => s.Trim().Length != s.Length;

        private static string RemoveLeadingAndTrailingWhitespaces(string s)
        {
            Debug.Assert(HasLeadingOrTrailingWhitespaces(s));
            return s.Trim();
        }

        private static void AnalyzeHelpLinkUri(
            OperationAnalysisContext operationAnalysisContext,
            ImmutableArray<IArgumentOperation> creationArguments,
            out string? helpLink)
        {
            helpLink = null;

            // Find the matching argument for helpLinkUri
            foreach (var argument in creationArguments)
            {
                if (argument.Parameter.Name.Equals(HelpLinkUriParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    if (argument.Value.ConstantValue.HasValue)
                    {
                        helpLink = argument.Value.ConstantValue.Value as string;
                        if (helpLink == null)
                        {
                            Diagnostic diagnostic = argument.CreateDiagnostic(ProvideHelpUriInDescriptorRule);
                            operationAnalysisContext.ReportDiagnostic(diagnostic);
                        }
                    }

                    return;
                }
            }
        }

        private static void AnalyzeCustomTags(OperationAnalysisContext operationAnalysisContext, ImmutableArray<IArgumentOperation> creationArguments)
        {
            // Find the matching argument for customTags
            foreach (var argument in creationArguments)
            {
                if (argument.Parameter.Name.Equals(CustomTagsParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    if (argument.Value is IArrayCreationOperation arrayCreation &&
                        arrayCreation.DimensionSizes.Length == 1 &&
                        arrayCreation.DimensionSizes[0].ConstantValue.HasValue &&
                        arrayCreation.DimensionSizes[0].ConstantValue.Value is int size &&
                        size == 0)
                    {
                        Diagnostic diagnostic = argument.CreateDiagnostic(ProvideCustomTagsInDescriptorRule);
                        operationAnalysisContext.ReportDiagnostic(diagnostic);
                    }

                    return;
                }
            }
        }

        private static (bool? isEnabledByDefault, DiagnosticSeverity? defaultSeverity) GetDefaultSeverityAndEnabledByDefault(Compilation compilation, ImmutableArray<IArgumentOperation> creationArguments)
        {
            var diagnosticSeverityType = compilation.GetOrCreateTypeByMetadataName(typeof(DiagnosticSeverity).FullName);
            var ruleLevelType = compilation.GetOrCreateTypeByMetadataName(typeof(RuleLevel).FullName);

            bool? isEnabledByDefault = null;
            DiagnosticSeverity? defaultSeverity = null;

            foreach (var argument in creationArguments)
            {
                switch (argument.Parameter.Name)
                {
                    case IsEnabledByDefaultParameterName:
                        if (argument.Value.ConstantValue.HasValue)
                        {
                            isEnabledByDefault = (bool)argument.Value.ConstantValue.Value;
                        }

                        break;

                    case DefaultSeverityParameterName:
                        if (argument.Value is IFieldReferenceOperation fieldReference &&
                            fieldReference.Field.ContainingType.Equals(diagnosticSeverityType) &&
                            Enum.TryParse(fieldReference.Field.Name, out DiagnosticSeverity parsedSeverity))
                        {
                            defaultSeverity = parsedSeverity;
                        }

                        break;

                    case RuleLevelParameterName:
                        if (ruleLevelType != null &&
                            argument.Value is IFieldReferenceOperation fieldReference2 &&
                            fieldReference2.Field.ContainingType.Equals(ruleLevelType) &&
                            Enum.TryParse(fieldReference2.Field.Name, out RuleLevel parsedRuleLevel))
                        {
                            switch (parsedRuleLevel)
                            {
                                case RuleLevel.BuildWarning:
                                    defaultSeverity = DiagnosticSeverity.Warning;
                                    isEnabledByDefault = true;
                                    break;

                                case RuleLevel.IdeSuggestion:
                                    defaultSeverity = DiagnosticSeverity.Info;
                                    isEnabledByDefault = true;
                                    break;

                                case RuleLevel.IdeHidden_BulkConfigurable:
                                    defaultSeverity = DiagnosticSeverity.Hidden;
                                    isEnabledByDefault = true;
                                    break;

                                case RuleLevel.Disabled:
                                case RuleLevel.CandidateForRemoval:
                                    isEnabledByDefault = false;
                                    break;
                            }

                            return (isEnabledByDefault, defaultSeverity);
                        }

                        break;
                }
            }

            if (isEnabledByDefault == false)
            {
                defaultSeverity = null;
            }

            return (isEnabledByDefault, defaultSeverity);
        }

        private static void AnalyzeRuleId(
            OperationAnalysisContext operationAnalysisContext,
            ImmutableArray<IArgumentOperation> creationArguments,
            bool isAnalyzerReleaseTracking,
            ReleaseTrackingData? shippedData,
            ReleaseTrackingData? unshippedData,
            PooledConcurrentSet<string> seenRuleIds,
            AdditionalText? diagnosticCategoryAndIdRangeText,
            string? category,
            string analyzerName,
            string? helpLink,
            bool? isEnabledByDefault,
            DiagnosticSeverity? defaultSeverity,
            ImmutableArray<(string? prefix, int start, int end)> allowedIdsInfoListOpt,
            ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentBag<Location>>> idToAnalyzerMap)
        {
            var analyzer = ((IFieldSymbol)operationAnalysisContext.ContainingSymbol).ContainingType.OriginalDefinition;
            string? ruleId = null;
            foreach (var argument in creationArguments)
            {
                if (argument.Parameter.Name.Equals(DiagnosticIdParameterName, StringComparison.OrdinalIgnoreCase))
                {
                    // Check if diagnostic ID is a constant string.
                    if (argument.Value.ConstantValue.HasValue &&
                        argument.Value.Type != null &&
                        argument.Value.Type.SpecialType == SpecialType.System_String)
                    {
                        ruleId = (string)argument.Value.ConstantValue.Value;
                        seenRuleIds.Add(ruleId);

                        var location = argument.Value.Syntax.GetLocation();
                        static string GetAnalyzerName(INamedTypeSymbol a) => a.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

                        // Factory methods to track declaration locations for every analyzer rule ID.
                        ConcurrentBag<Location> AddLocationFactory(string analyzerName)
                            => new() { location };

                        ConcurrentBag<Location> UpdateLocationsFactory(string analyzerName, ConcurrentBag<Location> bag)
                        {
                            bag.Add(location);
                            return bag;
                        }

                        ConcurrentDictionary<string, ConcurrentBag<Location>> AddNamedTypeFactory(string r)
                        {
                            var dict = new ConcurrentDictionary<string, ConcurrentBag<Location>>();
                            dict.AddOrUpdate(
                                key: GetAnalyzerName(analyzer),
                                addValueFactory: AddLocationFactory,
                                updateValueFactory: UpdateLocationsFactory);
                            return dict;
                        }

                        ConcurrentDictionary<string, ConcurrentBag<Location>> UpdateNamedTypeFactory(string r, ConcurrentDictionary<string, ConcurrentBag<Location>> existingValue)
                        {
                            existingValue.AddOrUpdate(
                                key: GetAnalyzerName(analyzer),
                                addValueFactory: AddLocationFactory,
                                updateValueFactory: UpdateLocationsFactory);
                            return existingValue;
                        }

                        idToAnalyzerMap.AddOrUpdate(
                            key: ruleId,
                            addValueFactory: AddNamedTypeFactory,
                            updateValueFactory: UpdateNamedTypeFactory);

                        if (IsReservedDiagnosticId(ruleId, operationAnalysisContext.Compilation.AssemblyName))
                        {
                            operationAnalysisContext.ReportDiagnostic(argument.Value.Syntax.CreateDiagnostic(DoNotUseReservedDiagnosticIdRule, ruleId));
                        }

                        // If we have an additional file specifying required range and/or format for the ID, validate the ID.
                        if (!allowedIdsInfoListOpt.IsDefault)
                        {
                            AnalyzeAllowedIdsInfoList(ruleId, argument, diagnosticCategoryAndIdRangeText, category, allowedIdsInfoListOpt, operationAnalysisContext.ReportDiagnostic);
                        }

                        // If we have an additional file specifying required range and/or format for the ID, validate the ID.
                        if (isAnalyzerReleaseTracking)
                        {
                            RoslynDebug.Assert(shippedData != null);
                            RoslynDebug.Assert(unshippedData != null);

                            AnalyzeAnalyzerReleases(ruleId, argument, category, analyzerName, helpLink, isEnabledByDefault,
                                defaultSeverity, shippedData, unshippedData, operationAnalysisContext.ReportDiagnostic);
                        }
                        else if (shippedData == null && unshippedData == null)
                        {
                            var diagnostic = argument.CreateDiagnostic(EnableAnalyzerReleaseTrackingRule, ruleId);
                            operationAnalysisContext.ReportDiagnostic(diagnostic);
                        }
                    }
                    else
                    {
                        // Diagnostic Id for rule '{0}' must be a non-null constant.
                        string arg1 = ((IFieldInitializerOperation)operationAnalysisContext.Operation).InitializedFields.Single().Name;
                        var diagnostic = argument.Value.CreateDiagnostic(DiagnosticIdMustBeAConstantRule, arg1);
                        operationAnalysisContext.ReportDiagnostic(diagnostic);
                    }

                    return;
                }
            }
        }

        private static bool IsReservedDiagnosticId(string ruleId, string assemblyName)
        {
            if (ruleId.Length < 3)
            {
                return false;
            }

            var isCARule = ruleId.StartsWith("CA", StringComparison.Ordinal);

            if (!isCARule &&
                !ruleId.StartsWith("CS", StringComparison.Ordinal) &&
                !ruleId.StartsWith("BC", StringComparison.Ordinal))
            {
                return false;
            }

            if (!ruleId[2..].All(c => char.IsDigit(c)))
            {
                return false;
            }

            return !isCARule || !CADiagnosticIdAllowedAssemblies.Contains(assemblyName);
        }
    }
}
