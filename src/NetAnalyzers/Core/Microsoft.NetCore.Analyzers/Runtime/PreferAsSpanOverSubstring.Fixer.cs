﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Operations;
using RequiredSymbols = Microsoft.NetCore.Analyzers.Runtime.PreferAsSpanOverSubstring.RequiredSymbols;

namespace Microsoft.NetCore.Analyzers.Runtime
{
    public abstract class PreferAsSpanOverSubstringFixer : CodeFixProvider
    {
        private const string SubstringStartIndexArgumentName = "startIndex";
        private const string AsSpanStartArgumentName = "start";

        private protected abstract void ReplaceInvocationMethodName(SyntaxEditor editor, SyntaxNode memberInvocation, string newName);

        private protected abstract void ReplaceNamedArgumentName(SyntaxEditor editor, SyntaxNode invocation, string oldArgumentName, string newArgumentName);

        private protected abstract bool IsNamespaceImported(DocumentEditor editor, string namespaceName);

        public sealed override ImmutableArray<string> FixableDiagnosticIds => ImmutableArray.Create(PreferAsSpanOverSubstring.RuleId);

        public sealed override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var document = context.Document;
            var token = context.CancellationToken;
            SyntaxNode root = await document.GetSyntaxRootAsync(token).ConfigureAwait(false);
            SemanticModel model = await document.GetSemanticModelAsync(token).ConfigureAwait(false);
            var compilation = model.Compilation;

            if (root.FindNode(context.Span, getInnermostNodeForTie: true) is not SyntaxNode reportedNode || model.GetOperation(reportedNode, token) is not IInvocationOperation reportedInvocation)
                return;
            if (!RequiredSymbols.TryGetSymbols(compilation, out RequiredSymbols symbols))
                return;
            if (!symbols.TryGetEquivalentSpanBasedOverload(reportedInvocation, out IMethodSymbol? spanBasedOverload))
                return;

            string title = MicrosoftNetCoreAnalyzersResources.PreferAsSpanOverSubstringTitle;
            var codeAction = CodeAction.Create(title, CreateChangedDocument, title);
            context.RegisterCodeFix(codeAction, context.Diagnostics);

            async Task<Document> CreateChangedDocument(CancellationToken token)
            {
                var editor = await DocumentEditor.CreateAsync(document, token).ConfigureAwait(false);

                foreach (var argument in reportedInvocation.Arguments)
                {
                    IOperation value = PreferAsSpanOverSubstring.WalkDownImplicitConversions(argument.Value);

                    //  Convert Substring invocations to equivalent AsSpan invocations.
                    if (symbols.IsAnySubstringInvocation(value))
                    {
                        ReplaceInvocationMethodName(editor, value.Syntax, nameof(MemoryExtensions.AsSpan));
                        //  Ensure named Substring arguments get renamed to their equivalent AsSpan counterparts.
                        ReplaceNamedArgumentName(editor, value.Syntax, SubstringStartIndexArgumentName, AsSpanStartArgumentName);
                    }

                    //  Ensure named arguments on the original overload are renamed to their 
                    //  ordinal counterparts on the new overload.
                    string oldArgumentName = argument.Parameter.Name;
                    string newArgumentName = spanBasedOverload.Parameters[argument.Parameter.Ordinal].Name;
                    ReplaceNamedArgumentName(editor, reportedInvocation.Syntax, oldArgumentName, newArgumentName);
                }

                //  Import System namespace if necessary.
                if (!IsNamespaceImported(editor, nameof(System)))
                {
                    SyntaxNode withoutSystemImport = editor.GetChangedRoot();
                    SyntaxNode systemNamespaceImportStatement = editor.Generator.NamespaceImportDeclaration(nameof(System));
                    SyntaxNode withSystemImport = editor.Generator.AddNamespaceImports(withoutSystemImport, systemNamespaceImportStatement);
                    editor.ReplaceNode(editor.OriginalRoot, withSystemImport);
                }

                return editor.GetChangedDocument();
            }
        }

        public sealed override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;
    }
}
