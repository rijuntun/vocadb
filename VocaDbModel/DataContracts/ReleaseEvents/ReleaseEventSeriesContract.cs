﻿using System.Runtime.Serialization;
using VocaDb.Model.Domain;
using VocaDb.Model.Domain.Images;
using VocaDb.Model.Domain.ReleaseEvents;

namespace VocaDb.Model.DataContracts.ReleaseEvents {

	[DataContract(Namespace = Schemas.VocaDb)]
	public class ReleaseEventSeriesContract : IEntryImageInformation, IEntryWithIntId {

		EntryType IEntryImageInformation.EntryType {
			get { return EntryType.ReleaseEventSeries; }
		}

		string IEntryImageInformation.Mime {
			get { return PictureMime; }
		}

		int IEntryImageInformation.Version {
			get { return 0; }
		}

		public ReleaseEventSeriesContract() {
			Description = string.Empty;
		}

		public ReleaseEventSeriesContract(ReleaseEventSeries series)
			: this() {

			ParamIs.NotNull(() => series);

			Description = series.Description;
			Id = series.Id;
			Name = series.Name;
			PictureMime = series.PictureMime;

		}

		[DataMember]
		public string Description { get; set; }

		[DataMember]
		public int Id { get; set; }

		[DataMember]
		public string Name { get; set; }

		[DataMember]
		public string PictureMime { get; set; }

		public override string ToString() {
			return string.Format("release event series {0} [{1}]", Name, Id);
		}

	}

}
