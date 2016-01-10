﻿using System.Runtime.Serialization;
using VocaDb.Model.Domain.Globalization;
using VocaDb.Model.Domain.Tags;

namespace VocaDb.Model.DataContracts.Tags {

	[DataContract(Namespace = Schemas.VocaDb)]
	public class TagBaseContract : ITag {

		public TagBaseContract() { }

		public TagBaseContract(Tag tag, ContentLanguagePreference languagePreference) {
			
			ParamIs.NotNull(() => tag);

			Id = tag.Id;
			Name = tag.Names.SortNames[languagePreference];
			UrlSlug = tag.UrlSlug;

		}

		[DataMember]
		public int Id { get; set; }

		[DataMember]
		public string Name { get; set; }

		[DataMember]
		public string UrlSlug { get; set; }

	}

}
