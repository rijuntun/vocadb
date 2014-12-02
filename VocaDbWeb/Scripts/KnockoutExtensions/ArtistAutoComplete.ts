/// <reference path="../typings/knockout/knockout.d.ts" />
/// <reference path="../Shared/GlobalFunctions.ts" />
/// <reference path="../Shared/EntrySearchDrop.d.ts" />
/// <reference path="AutoCompleteParams.ts" />

interface KnockoutBindingHandlers {
    artistAutoComplete: KnockoutBindingHandler;
}

// Artist autocomplete search box.
ko.bindingHandlers.artistAutoComplete = {
    init: (element: HTMLElement, valueAccessor) => {

        var properties: vdb.knockoutExtensions.AutoCompleteParams = ko.utils.unwrapObservable(valueAccessor());

		var filter = properties.filter;

		if (properties.ignoreId) {

			filter = (item: vdb.dataContracts.ArtistContract) => {

				if (properties.ignoreId && item.id == properties.ignoreId) {
					return false;
				}

				return properties.filter != null ? properties.filter(item) : true;

			}

		}

		var queryParams = { nameMatchMode: 'Auto', lang: vdb.models.globalization.ContentLanguagePreference[vdb.values.languagePreference] };
		if (properties.extraQueryParams)
			jQuery.extend(queryParams, properties.extraQueryParams);

        initEntrySearch(element, null, "Artist", vdb.functions.mapAbsoluteUrl("/api/artists"),
            {
                allowCreateNew: properties.allowCreateNew,
				acceptSelection: properties.acceptSelection,
				createNewItem: properties.createNewItem,
				createOptionFirstRow: (item: vdb.dataContracts.ArtistContract) => (item.name + " (" + item.artistType + ")"),
				createOptionSecondRow: (item: vdb.dataContracts.ArtistContract) => (item.additionalNames),
				extraQueryParams: queryParams,
				filter: filter,
                height: properties.height,
				termParamName: 'query',
				method: 'GET'
            });


    }
}