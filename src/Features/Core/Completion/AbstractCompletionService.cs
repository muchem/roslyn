﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.Completion.Rules;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Snippets;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Completion
{
    internal abstract partial class AbstractCompletionService : ICompletionService, ITextCompletionService
    {
        private static readonly Func<string, List<CompletionItem>> s_createList = _ => new List<CompletionItem>();

        private const int MruSize = 10;

        private readonly List<string> _committedItems = new List<string>(MruSize);
        private readonly object _mruGate = new object();

        private void CompletionItemCommitted(CompletionItem item)
        {
            lock (_mruGate)
            {
                // We need to remove the item if it's already in the list.
                // If we're at capacity, we need to remove the LRU item.
                var removed = _committedItems.Remove(item.DisplayText);
                if (!removed && _committedItems.Count == MruSize)
                {
                    _committedItems.RemoveAt(0);
                }

                _committedItems.Add(item.DisplayText);
            }
        }

        protected int GetMRUIndex(CompletionItem item)
        {
            lock (_mruGate)
            {
                // A lower value indicates more recently used.  Since items are added
                // to the end of the list, our result just maps to the negation of the 
                // index.
                // -1 => 1  == Not Found
                // 0  => 0  == least recently used 
                // 9  => -9 == most recently used 
                var index = _committedItems.IndexOf(item.DisplayText);
                return -index;
            }
        }

        public void ClearMRUCache()
        {
            lock (_mruGate)
            {
                _committedItems.Clear();
            }
        }

        /// <summary>
        /// Apply any culture-specific quirks to the given text for the purposes of pattern matching.
        /// For example, in the Turkish locale, capital 'i's should be treated specially in Visual Basic.
        /// </summary>
        public virtual string GetCultureSpecificQuirks(string candidate)
        {
            return candidate;
        }

        public abstract IEnumerable<CompletionListProvider> GetDefaultCompletionProviders();

        protected abstract string GetLanguageName();

        public async Task<CompletionList> GetCompletionListAsync(
            Document document,
            int position,
            CompletionTriggerInfo triggerInfo,
            IEnumerable<CompletionListProvider> completionProviders,
            CancellationToken cancellationToken)
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return await GetCompletionListAsync(document, text, position, triggerInfo, completionProviders, document.Project.Solution.Workspace.Options, cancellationToken).ConfigureAwait(false);
        }

        public Task<CompletionList> GetCompletionListAsync(
            SourceText text,
            int position,
            CompletionTriggerInfo triggerInfo,
            IEnumerable<CompletionListProvider> completionProviders,
            OptionSet options,
            CancellationToken cancellationToken)
        {
            return GetCompletionListAsync(null, text, position, triggerInfo, completionProviders, options, cancellationToken);
        }

        private class ProviderList
        {
            public CompletionListProvider Provider;
            public CompletionList List;
        }

        private async Task<CompletionList> GetCompletionListAsync(
            Document documentOpt,
            SourceText text,
            int position,
            CompletionTriggerInfo triggerInfo,
            IEnumerable<CompletionListProvider> completionProviders,
            OptionSet options,
            CancellationToken cancellationToken)
        {
            completionProviders = completionProviders ?? this.GetDefaultCompletionProviders();
            var completionProviderToIndex = GetCompletionProviderToIndex(completionProviders);
            var completionRules = GetDefaultCompletionRules();

            IEnumerable<CompletionListProvider> triggeredProviders;
            switch (triggerInfo.TriggerReason)
            {
                case CompletionTriggerReason.TypeCharCommand:
                    triggeredProviders = completionProviders.Where(p => p.IsTriggerCharacter(text, position - 1, options)).ToList();
                    break;
                case CompletionTriggerReason.BackspaceOrDeleteCommand:
                    triggeredProviders = this.TriggerOnBackspace(text, position, triggerInfo, options)
                        ? completionProviders
                        : SpecializedCollections.EmptyEnumerable<CompletionListProvider>();
                    break;
                default:
                    triggeredProviders = completionProviders;
                    break;
            }

            // Now, ask all the triggered providers if they can provide a group.
            var providersAndLists = new List<ProviderList>();
            foreach (var provider in triggeredProviders)
            {
                var completionList = await GetCompletionListAsync(provider, documentOpt, text, position, triggerInfo, cancellationToken).ConfigureAwait(false);
                if (completionList != null)
                {
                    providersAndLists.Add(new ProviderList { Provider = provider, List = completionList });
                }
            }

            // See if there was a group provided that was exclusive and had items in it.  If so, then
            // that's all we'll return.
            var firstExclusiveList = providersAndLists.FirstOrDefault(
                t => t.List.IsExclusive && t.List.Items.Any());

            if (firstExclusiveList != null)
            {
                return MergeAndPruneCompletionLists(SpecializedCollections.SingletonEnumerable(firstExclusiveList.List), completionRules);
            }

            // If no exclusive providers provided anything, then go through the remaining
            // triggered list and see if any provide items.
            var nonExclusiveLists = providersAndLists.Where(t => !t.List.IsExclusive).ToList();

            // If we still don't have any items, then we're definitely done.
            if (!nonExclusiveLists.Any(g => g.List.Items.Any()))
            {
                return null;
            }

            // If we do have items, then ask all the other (non exclusive providers) if they
            // want to augment the items.
            var usedProviders = nonExclusiveLists.Select(g => g.Provider);
            var nonUsedProviders = completionProviders.Except(usedProviders);
            var nonUsedNonExclusiveProviders = new List<ProviderList>();
            foreach (var provider in nonUsedProviders)
            {
                var completionList = await GetCompletionListAsync(provider, documentOpt, text, position, triggerInfo, cancellationToken).ConfigureAwait(false);
                if (completionList != null && !completionList.IsExclusive)
                {
                    nonUsedNonExclusiveProviders.Add(new ProviderList { Provider = provider, List = completionList });
                }
            }

            var allProvidersAndLists = nonExclusiveLists.Concat(nonUsedNonExclusiveProviders).ToList();
            if (allProvidersAndLists.Count == 0)
            {
                return null;
            }

            // Providers are ordered, but we processed them in our own order.  Ensure that the
            // groups are properly ordered based on the original providers.
            allProvidersAndLists.Sort((p1, p2) => completionProviderToIndex[p1.Provider] - completionProviderToIndex[p2.Provider]);
            return MergeAndPruneCompletionLists(allProvidersAndLists.Select(g => g.List), completionRules);
        }

        private static CompletionList MergeAndPruneCompletionLists(IEnumerable<CompletionList> completionLists, ICompletionRules completionRules)
        {
            var displayNameToItemsMap = new Dictionary<string, List<CompletionItem>>();
            CompletionItem builder = null;

            foreach (var completionList in completionLists)
            {
                if (completionList != null)
                {
                    foreach (var item in completionList.Items.WhereNotNull())
                    {
                        // New items that match an existing item will replace it.  
                        ReplaceExistingItem(item, displayNameToItemsMap, completionRules);
                    }

                    builder = builder ?? completionList.Builder;
                }
            }

            if (displayNameToItemsMap.Count == 0)
            {
                return null;
            }

            var totalItems = displayNameToItemsMap.Values.Flatten().ToList();
            totalItems.Sort();

            // TODO(DustinCa): This is lossy -- we lose the IsExclusive field. Fix that.

            return new CompletionList(totalItems, builder);
        }

        private static void ReplaceExistingItem(
            CompletionItem item,
            Dictionary<string, List<CompletionItem>> displayNameToItemsMap,
            ICompletionRules completionRules)
        {
            // See if we have an item with 
            var sameNamedItems = displayNameToItemsMap.GetOrAdd(item.DisplayText, s_createList);
            for (int i = 0; i < sameNamedItems.Count; i++)
            {
                var existingItem = sameNamedItems[i];

                Contract.Assert(item.DisplayText == existingItem.DisplayText);

                if (completionRules.ItemsMatch(item, existingItem).Value)
                {
                    sameNamedItems[i] = Disambiguate(item, existingItem);
                    return;
                }
            }

            sameNamedItems.Add(item);
        }

        private static CompletionItem Disambiguate(CompletionItem item, CompletionItem existingItem)
        {
            // We've constructed the export order of completion providers so 
            // that snippets are exported after everything else. That way,
            // when we choose a single item per display text, snippet 
            // glyphs appear by snippets. This breaks preselection of items
            // whose display text is also a snippet (workitem 852578),
            // the snippet item doesn't have its preselect bit set.
            // We'll special case this by not preferring later items
            // if they are snippets and the other candidate is preselected.
            if (existingItem.Preselect && item.CompletionProvider is ISnippetCompletionProvider)
            {
                return existingItem;
            }

            // If one is a keyword, and the other is some other item that inserts the same text as the keyword,
            // keep the keyword
            var keywordItem = existingItem as KeywordCompletionItem ?? item as KeywordCompletionItem;
            if (keywordItem != null)
            {
                return keywordItem;
            }

            return item;
        }

        private Dictionary<CompletionListProvider, int> GetCompletionProviderToIndex(IEnumerable<CompletionListProvider> completionProviders)
        {
            var result = new Dictionary<CompletionListProvider, int>();

            int i = 0;
            foreach (var completionProvider in completionProviders)
            {
                result[completionProvider] = i;
                i++;
            }

            return result;
        }

        private static async Task<CompletionList> GetCompletionListAsync(
            CompletionListProvider provider,
            Document documentOpt,
            SourceText text,
            int position,
            CompletionTriggerInfo triggerInfo,
            CancellationToken cancellationToken)
        {
            if (provider is TextCompletionProvider)
            {
                return ((TextCompletionProvider)provider).GetCompletionList(text, position, triggerInfo, cancellationToken);
            }

            if (documentOpt != null)
            {
                var itemsBuilder = ImmutableArray.CreateBuilder<CompletionItem>();
                Action<CompletionItem> addCompletionItem = item =>
                {
                    itemsBuilder.Add(item);
                };

                CompletionItem builder = null;
                Action<CompletionItem> registerBuilder = item =>
                {
                    builder = item;
                };

                var isExclusive = false;
                Action<bool> makeExclusive = value =>
                {
                    isExclusive = value;
                };

                var context = new CompletionListContext(documentOpt, position, triggerInfo, addCompletionItem, registerBuilder, makeExclusive, cancellationToken);

                await provider.RegisterCompletionListAsync(context).ConfigureAwait(false);

                return new CompletionList(itemsBuilder.AsImmutable(), builder, isExclusive);
            }

            Contract.Fail("Should never get here.");

            return null;
        }

        public bool IsTriggerCharacter(SourceText text, int characterPosition, IEnumerable<CompletionListProvider> completionProviders, OptionSet options)
        {
            if (!options.GetOption(CompletionOptions.TriggerOnTyping, GetLanguageName()))
            {
                return false;
            }

            completionProviders = completionProviders ?? GetDefaultCompletionProviders();
            return completionProviders.Any(p => p.IsTriggerCharacter(text, characterPosition, options));
        }

        protected abstract bool TriggerOnBackspace(SourceText text, int position, CompletionTriggerInfo triggerInfo, OptionSet options);

        public abstract Task<TextSpan> GetDefaultTrackingSpanAsync(Document document, int position, CancellationToken cancellationToken);

        public virtual ICompletionRules GetDefaultCompletionRules()
        {
            return new CompletionRules(this);
        }

        public virtual bool DismissIfEmpty
        {
            get { return false; }
        }

        public Task<string> GetSnippetExpansionNoteForCompletionItemAsync(CompletionItem completionItem, Workspace workspace)
        {
            var insertionText = completionItem.CompletionProvider.GetTextChange(completionItem, '\t').NewText;

            var snippetInfoService = workspace.Services.GetLanguageServices(GetLanguageName()).GetService<ISnippetInfoService>();
            if (snippetInfoService != null && snippetInfoService.SnippetShortcutExists_NonBlocking(insertionText))
            {
                return Task.FromResult(string.Format(FeaturesResources.NoteTabTwiceToInsertTheSnippet, insertionText));
            }

            return SpecializedTasks.Default<string>();
        }

        public virtual bool SupportSnippetCompletionListOnTab
        {
            get { return false; }
        }

        public virtual bool DismissIfLastFilterCharacterDeleted
        {
            get { return false; }
        }
    }
}
