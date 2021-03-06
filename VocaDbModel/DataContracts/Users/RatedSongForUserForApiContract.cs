﻿using System;
using System.Runtime.Serialization;
using VocaDb.Model.DataContracts.Songs;
using VocaDb.Model.Domain.Globalization;
using VocaDb.Model.Domain.Songs;
using VocaDb.Model.Domain.Users;

namespace VocaDb.Model.DataContracts.Users {

	[DataContract(Namespace = Schemas.VocaDb)]
	public class RatedSongForUserForApiContract : SongRatingContract {

		public RatedSongForUserForApiContract() { }

		public RatedSongForUserForApiContract(FavoriteSongForUser ratedSong, IUserIconFactory userIconFactory, UserOptionalFields userFields) {

			this.Date = ratedSong.Date;
			this.Rating = ratedSong.Rating;
			if (ratedSong.User.Options.PublicRatings) {
				User = new UserForApiContract(ratedSong.User, userIconFactory, userFields);
			}

		}

		public RatedSongForUserForApiContract(FavoriteSongForUser ratedSong, ContentLanguagePreference languagePreference, SongOptionalFields fields) {

			this.Date = ratedSong.Date;
			this.Rating = ratedSong.Rating;
			this.Song = new SongForApiContract(ratedSong.Song, null, languagePreference, fields);

		}

		[DataMember]
		public DateTime Date { get; set; }

		[DataMember(EmitDefaultValue = false)]
		public SongForApiContract Song { get; set; }

		[DataMember(EmitDefaultValue = false)]
		public UserForApiContract User { get; set; }

	}

	[DataContract(Namespace = Schemas.VocaDb)]
	public class SongRatingContract {

		[DataMember]
		public SongVoteRating Rating { get; set; }

	}

}
