﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Runtime.Caching;
using System.Web;
using NHibernate;
using NLog;
using VocaDb.Model.Database.Queries.Partial;
using VocaDb.Model.Database.Repositories;
using VocaDb.Model.DataContracts;
using VocaDb.Model.DataContracts.PVs;
using VocaDb.Model.DataContracts.ReleaseEvents;
using VocaDb.Model.DataContracts.Songs;
using VocaDb.Model.DataContracts.UseCases;
using VocaDb.Model.DataContracts.Users;
using VocaDb.Model.Domain;
using VocaDb.Model.Domain.Activityfeed;
using VocaDb.Model.Domain.Albums;
using VocaDb.Model.Domain.Artists;
using VocaDb.Model.Domain.Caching;
using VocaDb.Model.Domain.ExtLinks;
using VocaDb.Model.Domain.Globalization;
using VocaDb.Model.Domain.PVs;
using VocaDb.Model.Domain.ReleaseEvents;
using VocaDb.Model.Domain.Security;
using VocaDb.Model.Domain.Songs;
using VocaDb.Model.Domain.Tags;
using VocaDb.Model.Domain.Users;
using VocaDb.Model.Helpers;
using VocaDb.Model.Service;
using VocaDb.Model.Service.EntryValidators;
using VocaDb.Model.Service.Helpers;
using VocaDb.Model.Service.Queries;
using VocaDb.Model.Service.QueryableExtenders;
using VocaDb.Model.Service.Translations;
using VocaDb.Model.Service.VideoServices;
using VocaDb.Model.Utils;
using VocaDb.Model.Utils.Config;

namespace VocaDb.Model.Database.Queries {

	/// <summary>
	/// Database queries related to <see cref="Song"/>.
	/// </summary>
	public class SongQueries : QueriesBase<ISongRepository, Song> {

		class SongTupleEqualityComparer<T> : IEqualityComparer<Tuple<Song, T>> {
			public bool Equals(Tuple<Song, T> x, Tuple<Song, T> y) {
				return Equals(x.Item1, y.Item1);
			}

			public int GetHashCode(Tuple<Song, T> obj) {
				return obj.Item1.GetHashCode();
			}
		}

		private static readonly Logger log = LogManager.GetCurrentClassLogger();
		private readonly ObjectCache cache;
		private readonly VdbConfigManager config;
		private readonly IEntryLinkFactory entryLinkFactory;
		private readonly IEnumTranslations enumTranslations;
		private readonly ILanguageDetector languageDetector;
		private readonly IUserMessageMailer mailer;
		private readonly IPVParser pvParser;
		private readonly IUserIconFactory userIconFactory;

		private void AddSongHit(IDatabaseContext<Song> session, Song song, string hostname) {

			if (!PermissionContext.IsLoggedIn && string.IsNullOrEmpty(hostname))
				return;

			var agentNum = PermissionContext.IsLoggedIn ? PermissionContext.LoggedUserId : hostname.GetHashCode();

			if (agentNum == 0)
				return;

			using (var tx = session.BeginTransaction(IsolationLevel.ReadUncommitted)) {

				var songId = song.Id;
				var isHit = session.Query<SongHit>().Any(h => h.Song.Id == songId && h.Agent == agentNum);

				if (!isHit) {
					var hit = new SongHit(song, agentNum);
					session.Save(hit);
				}

				try {
					tx.Commit();
				} catch (TransactionException x) {
					log.Error(x, "Unable to save song hit");
				}

			}

		}
		private void AddTagsFromPV(VideoUrlParseResult pvResult, Song song, IDatabaseContext<Song> ctx) {
			
			if (pvResult.Tags == null || !pvResult.Tags.Any())
				return;
						
			var user = ctx.OfType<User>().GetLoggedUser(PermissionContext);
			var tags = GetTags(ctx.OfType<Tag>(), pvResult.Tags);

			foreach (var tag in tags) {

				if (song.SongType == SongType.Cover && tag.Id == config.SpecialTags.Cover) {
					continue;
				}
							
				var usage = song.AddTag(tag);

				// Only add the vote if the tag usage isn't added yet.
				if (usage.IsNew) {

					ctx.Save(usage.Result);

					var vote = usage.Result.CreateVote(user);
					ctx.Save(vote);

					ctx.Update(usage.Result.Tag);

				}

			}

		}

		private Artist GetArtist(string name, IDatabaseContext<PVForSong> ctx, ArtistType[] preferredArtistTypes) {

			if (preferredArtistTypes != null && preferredArtistTypes.Any()) {
				
				var preferredArtist = ctx.OfType<ArtistName>()
					.Query()
					.Where(n => n.Value == name && !n.Artist.Deleted && preferredArtistTypes.Contains(n.Artist.ArtistType))
					.Select(n => n.Artist)
					.FirstOrDefault();

				if (preferredArtist != null)
					return preferredArtist;

			}

			return ctx.OfType<ArtistName>()
				.Query()
				.Where(n => n.Value == name && !n.Artist.Deleted)
				.Select(n => n.Artist)
				.FirstOrDefault();

		}

		private SongMergeRecord GetMergeRecord(IDatabaseContext<Song> session, int sourceId) {
			return session.Query<SongMergeRecord>().FirstOrDefault(s => s.Source == sourceId);
		}

		private Song[] GetSongSuggestions(IDatabaseContext<Song> ctx, Song song) {

			var cacheKey = string.Format("GetSongSuggestions.{0}", song.Id);

			var songIds = cache.GetOrInsert(cacheKey, CachePolicy.AbsoluteExpiration(24), () => {
				var related = new RelatedSongsQuery(ctx).GetLikeMatches(song, new[] { song.Id }, 4);
				return related;
			});

			if (!songIds.Any())
				return new Song[0];

			return ctx.Query()
				.Where(s => songIds.Contains(s.Id))
				.ToArray();

		}

		private SongListBaseContract[] GetSongPools(IDatabaseContext<Song> ctx, int songId) {

			var cacheKey = string.Format("GetSongPools.{0}", songId);
			return cache.GetOrInsert(cacheKey, CachePolicy.AbsoluteExpiration(1), () => {

				var lists = ctx
					.Query<SongList>()
					.Where(s => s.FeaturedCategory == SongListFeaturedCategory.Pools && s.AllSongs.Any(l => l.Song.Id == songId))
					.OrderBy(s => s.Name)
					.Take(3)
					.ToArray();

				return lists.Select(s => new SongListBaseContract(s)).ToArray();

			});

		}

		private Tag[] GetTags(IDatabaseContext<Tag> session, string[] tagNames) {

			var direct = session.Query().WhereHasName(tagNames).ToArray();
			return direct;

		}

		private bool HasCoverTag(IDatabaseContext<PVForSong> ctx, VideoUrlParseResult res) {

			if (config.SpecialTags.Cover == 0)
				return false;

			var coverTag = ctx.Load<Tag>(config.SpecialTags.Cover);
			return res.Tags.Any(t => coverTag.Names.AllValues.Contains(t));

		}

		private NicoTitleParseResult ParseNicoPV(IDatabaseContext<PVForSong> ctx, VideoUrlParseResult res) {

			if (res == null || !res.IsOk)
				return null;

			var titleParseResult = NicoHelper.ParseTitle(res.Title, a => GetArtist(a, ctx, AppConfig.PreferredNicoArtistTypes));
			
			if (!string.IsNullOrEmpty(titleParseResult.Title))
				titleParseResult.TitleLanguage = languageDetector.Detect(titleParseResult.Title, ContentLanguageSelection.Unspecified);

			if (titleParseResult.SongType == SongType.Unspecified) {
				titleParseResult.SongType = HasCoverTag(ctx, res) ? SongType.Cover : SongType.Original;
			}

			if (!string.IsNullOrEmpty(res.AuthorId)) {

				var authorPage = NicoHelper.GetUserProfileUrlById(res.AuthorId);

				var author = ctx.OfType<ArtistWebLink>().Query()
					.Where(w => w.Url == authorPage && !w.Entry.Deleted)
					.Select(w => w.Entry)
					.FirstOrDefault();

				if (author != null && !titleParseResult.Artists.Contains(author))
					titleParseResult.Artists.Add(author);

			}

			return titleParseResult;

		}

		private VideoUrlParseResult ParsePV(IDatabaseContext<PVForSong> ctx, string url) {

			if (string.IsNullOrEmpty(url))
				return null;

			var pvResult = pvParser.ParseByUrl(url, true, PermissionContext);

			if (!pvResult.IsOk)
				throw pvResult.Exception;

			var existing = ctx.Query().FirstOrDefault(
				s => s.Service == pvResult.Service && s.PVId == pvResult.Id && !s.Song.Deleted);

			if (existing != null) {
				throw new VideoParseException(string.Format("Song '{0}' already contains this PV",
					existing.Song.TranslatedName[PermissionContext.LanguagePreference]));
			}

			return pvResult;

		}

		public SongQueries(ISongRepository repository, IUserPermissionContext permissionContext, IEntryLinkFactory entryLinkFactory, IPVParser pvParser, IUserMessageMailer mailer,
			ILanguageDetector languageDetector, IUserIconFactory userIconFactory, IEnumTranslations enumTranslations, ObjectCache cache, VdbConfigManager config)
			: base(repository, permissionContext) {

			this.entryLinkFactory = entryLinkFactory;
			this.pvParser = pvParser;
			this.mailer = mailer;
			this.languageDetector = languageDetector;
			this.userIconFactory = userIconFactory;
			this.enumTranslations = enumTranslations;
            this.cache = cache;
			this.config = config;

		}

		public ICommentQueries Comments(IDatabaseContext<Song> ctx) {
			return new CommentQueries<SongComment, Song>(ctx.OfType<SongComment>(), PermissionContext, userIconFactory, entryLinkFactory);
		}

		public ArchivedSongVersion Archive(IDatabaseContext<Song> ctx, Song song, SongDiff diff, SongArchiveReason reason, string notes = "") {

			var agentLoginData = ctx.CreateAgentLoginData(PermissionContext);
			var archived = ArchivedSongVersion.Create(song, diff, agentLoginData, reason, notes);
			ctx.OfType<ArchivedSongVersion>().Save(archived);
			return archived;

		}

		public ArchivedSongVersion Archive(IDatabaseContext<Song> ctx, Song song, SongArchiveReason reason, string notes = "") {

			return Archive(ctx, song, new SongDiff(), reason, notes);

		}

		public SongContract Create(CreateSongContract contract) {

			ParamIs.NotNull(() => contract);

			if (contract.Names == null || !contract.Names.Any())
				throw new ArgumentException("Song needs at least one name", nameof(contract));

			VerifyManageDatabase();

			return repository.HandleTransaction(ctx => {

				var pvResult = ParsePV(ctx.OfType<PVForSong>(), contract.PVUrl);
				var reprintPvResult = ParsePV(ctx.OfType<PVForSong>(), contract.ReprintPVUrl);

				ctx.AuditLogger.SysLog(string.Format("creating a new song with name '{0}'", contract.Names.First().Value));

				var diff = new SongDiff { Names = true };
				var song = new Song { SongType = contract.SongType };

				if (contract.OriginalVersion != null && contract.OriginalVersion.Id != 0) {
					song.OriginalVersion = ctx.Load(contract.OriginalVersion.Id);
					diff.OriginalVersion = true;
				}

				song.Names.Init(contract.Names, song);

				ctx.Save(song);

				foreach (var artistContract in contract.Artists) {

					if (artistContract.Artist != null) {
						var artist = ctx.OfType<Artist>().Load(artistContract.Artist.Id);
						if (!song.HasArtist(artist))
							ctx.OfType<ArtistForSong>().Save(song.AddArtist(artist, artistContract.IsSupport, artistContract.Roles));
					} else {
						ctx.OfType<ArtistForSong>().Save(song.AddArtist(artistContract.Name, artistContract.IsSupport, artistContract.Roles));
					}

				}

				diff.Artists = contract.Artists.Any();

				var pvs = new List<PVContract>();

				if (pvResult != null) {

					pvs.Add(new PVContract(pvResult, PVType.Original));

					AddTagsFromPV(pvResult, song, ctx);

				}

				if (reprintPvResult != null) {
					pvs.Add(new PVContract(reprintPvResult, PVType.Reprint));
				}

				var pvDiff = song.SyncPVs(pvs);
				ctx.OfType<PVForSong>().Sync(pvDiff);
				diff.PVs = pvs.Any();

				if (contract.WebLinks != null) {
					var weblinksDiff = ctx.Sync(WebLink.Sync(song.WebLinks, contract.WebLinks, song));
					diff.WebLinks = weblinksDiff.Changed;
				}

				if (contract.Lyrics != null && contract.Lyrics.Any()) {
					contract.Lyrics.ForEach(song.CreateLyrics);
					diff.Lyrics = true;
				}

				song.Status = (contract.Draft || !(new SongValidator().IsValid(song, config.SpecialTags.Instrumental))) ? EntryStatus.Draft : EntryStatus.Finished;

				song.UpdateArtistString();

				var archived = Archive(ctx, song, diff, SongArchiveReason.Created, contract.UpdateNotes ?? string.Empty);
				ctx.Update(song);

				ctx.AuditLogger.AuditLog(string.Format("created song {0} ({1})", entryLinkFactory.CreateEntryLink(song), song.SongType));
				AddEntryEditedEntry(ctx.OfType<ActivityEntry>(), song, EntryEditEvent.Created, archived);

				new FollowedArtistNotifier().SendNotifications(ctx.OfType<UserMessage>(), song, song.ArtistList, PermissionContext.LoggedUser, entryLinkFactory, mailer);

				return new SongContract(song, PermissionContext.LanguagePreference);

			});

		}

		public CommentForApiContract CreateComment(int songId, CommentForApiContract contract) {

			return HandleTransaction(ctx => Comments(ctx).Create(songId, contract));

		}

		public bool CreateReport(int songId, SongReportType reportType, string hostname, string notes, int? versionNumber) {

			ParamIs.NotNull(() => hostname);
			ParamIs.NotNull(() => notes);

			return HandleTransaction(ctx => {
				return new Model.Service.Queries.EntryReportQueries().CreateReport(ctx, PermissionContext,
					entryLinkFactory, report => report.Entry.Id == songId, 
					(song, reporter, notesTruncated) => new SongReport(song, reportType, reporter, hostname, notesTruncated, versionNumber),
					() => reportType != SongReportType.Other ? enumTranslations.SongReportTypeNames[reportType] : null,
					songId, reportType, hostname, notes);
			});

		}

		public CommentForApiContract[] GetComments(int songId) {
			
			return HandleQuery(ctx => Comments(ctx).GetAll(songId));

		}

		public LyricsForSongContract GetLyrics(int lyricsId) {
			return HandleQuery(ctx => new LyricsForSongContract(ctx.Load<LyricsForSong>(lyricsId)));
		}

		public SongContract GetSong(int id) {

			return HandleQuery(
				session => new SongContract(session.Load<Song>(id), PermissionContext.LanguagePreference));

		}

		public SongDetailsContract GetSongDetails(int songId, int albumId, string hostname, ContentLanguagePreference? languagePreference,
			IEnumerable<OptionalCultureCode> userLanguages) {

			return HandleQuery(session => {

				var lang = languagePreference ?? PermissionContext.LanguagePreference;
				var song = session.Load<Song>(songId);
				var contract = new SongDetailsContract(song, lang, GetSongPools(session, songId), 
					config.SpecialTags);
				var user = PermissionContext.LoggedUser;

				if (user != null) {

					var rating = session.Query<FavoriteSongForUser>()
						.FirstOrDefault(s => s.Song.Id == songId && s.User.Id == user.Id);

					contract.UserRating = (rating != null ? rating.Rating : SongVoteRating.Nothing);

				}

				contract.CommentCount = Comments(session).GetCount(songId);
				contract.LatestComments = session.Query<SongComment>()
					.Where(c => c.EntryForComment.Id == songId)
					.OrderByDescending(c => c.Created).Take(3).ToArray()
					.Select(c => new CommentForApiContract(c, userIconFactory)).ToArray();
				contract.Hits = session.Query<SongHit>().Count(h => h.Song.Id == songId);
				contract.ListCount = session.Query<SongInList>().Count(l => l.Song.Id == songId);
				contract.Suggestions = GetSongSuggestions(session, song).Select(s => new SongForApiContract(s, lang, SongOptionalFields.AdditionalNames | SongOptionalFields.ThumbUrl)).ToArray();

				contract.PreferredLyrics = LyricsHelper.GetDefaultLyrics(contract.LyricsFromParents, new OptionalCultureCode(CultureInfo.CurrentUICulture, true), userLanguages, 
					new Lazy<IEnumerable<UserKnownLanguage>>(() => session.OfType<User>().GetLoggedUserOrNull(permissionContext)?.KnownLanguages, false));

				if (albumId != 0) {

					var album = session.Load<Album>(albumId);

					var track = album.Songs.FirstOrDefault(s => song.Equals(s.Song));

					if (track != null) {

						contract.Album = new DataContracts.Albums.AlbumContract(album, lang);

						contract.AlbumSong = new SongInAlbumContract(track, lang, false);

						var previousIndex = album.PreviousTrackIndex(track.Index);
						var previous = album.Songs.FirstOrDefault(s => s.Index == previousIndex);
						contract.PreviousSong = previous != null && previous.Song != null ? new SongInAlbumContract(previous, lang, false) : null;

						var nextIndex = album.NextTrackIndex(track.Index);
						var next = album.Songs.FirstOrDefault(s => s.Index == nextIndex);
						contract.NextSong = next != null && next.Song != null ? new SongInAlbumContract(next, lang, false) : null;

					}

				}

				if (song.Deleted) {
					var mergeEntry = GetMergeRecord(session, songId);
					contract.MergedTo = (mergeEntry != null ? new SongContract(mergeEntry.Target, lang) : null);
				}

				AddSongHit(session, song, hostname);

				return contract;

			});

		}

		public SongWithPVAndVoteContract GetSongWithPVAndVote(int songId, bool addHit, string hostname = "") {

			return HandleQuery(session => {

				var song = session.Load<Song>(songId);
				FavoriteSongForUser vote = null;

				if (PermissionContext.IsLoggedIn) {
					var userId = PermissionContext.LoggedUserId;
					vote = session.Query<FavoriteSongForUser>().FirstOrDefault(s => s.Song.Id == songId && s.User.Id == userId);
				}

				if (addHit)
					AddSongHit(session, song, hostname);

				return new SongWithPVAndVoteContract(song, vote != null ? vote.Rating : SongVoteRating.Nothing, PermissionContext.LanguagePreference);

			});

		}

		public SongForApiContract GetSongForApi(int songId, SongOptionalFields fields, ContentLanguagePreference? lang = null) {

			return GetSongWithMergeRecord(songId, (song, r) => new SongForApiContract(song, r, lang ?? PermissionContext.LanguagePreference, fields));

		}

		public T GetSong<T>(int id, Func<Song, T> fac) {

			return HandleQuery(session => {
				var song = session.Load<Song>(id);
				return fac(song);
			});

		}

		public SongForEditContract GetSongForEdit(int songId) {

			return HandleQuery(session => new SongForEditContract(session.Load<Song>(songId), PermissionContext.LanguagePreference));

		}

		public T GetSongWithMergeRecord<T>(int id, Func<Song, SongMergeRecord, T> fac) {

			return HandleQuery(session => {
				var song = session.Load<Song>(id);
				return fac(song, (song.Deleted ? GetMergeRecord(session, id) : null));
			});

		}

		private IEnumerable<Tuple<Song, SongMatchProperty>> GetNameMatches(IDatabaseContext<Song> ctx, string[] names, int[] artistIds) {
			
			if (names == null || !names.Any())
				return Enumerable.Empty<Tuple<Song, SongMatchProperty>>();

			var nameMatchIds = new int[0];

			var totalCount = 10;

			if (artistIds != null && artistIds.Any()) {
				
				// Try match with both producer and title
				nameMatchIds = ctx.OfType<SongName>().Query()
					.Where(n => names.Contains(n.Value) && !n.Song.Deleted && n.Song.AllArtists.Any(a => a.Artist.ArtistType == ArtistType.Producer && artistIds.Contains(a.Artist.Id)))
					.Select(n => n.Song.Id)
					.OrderBy(s => s)
					.Distinct()
					.Take(totalCount)
					.ToArray();

			}

			if (nameMatchIds.Length < totalCount) {

				nameMatchIds = nameMatchIds.Union(ctx.OfType<SongName>().Query()
					.Where(n => names.Contains(n.Value) && !n.Song.Deleted)
					.Select(n => n.Song.Id)
					.OrderBy(s => s)
					.Distinct()
					.Take(totalCount - nameMatchIds.Length)
					.ToArray())
					.ToArray();
				
			}

			var nameMatches = (nameMatchIds.Any() ? CollectionHelper.SortByIds(ctx.Query().Where(s => nameMatchIds.Contains(s.Id)).ToArray(), nameMatchIds)
				.Select(d => new Tuple<Song, SongMatchProperty>(d, SongMatchProperty.Title)) : new Tuple<Song, SongMatchProperty>[] { });

			return nameMatches;

		}

		/// <summary>
		/// Parse song and find duplicates in the database.
		/// 
		/// Duplicates are searched based on the entered names and PVs.
		/// Additionally, if PVs are provided, those are parsed for song information such as title and artists.
		/// Titles from the PVs will be used for name matching as well.
		/// </summary>
		/// <param name="anyName">Song names to be searched for duplicates. Cannot be null. Can be empty.</param>
		/// <param name="anyPv">List of PVs to be searched for duplicates and parsed for song info. Cannot be null. Can be empty.</param>
		/// <param name="artistIds">List of artist IDs. Can be empty.</param>
		/// <param name="getPVInfo">
		/// Whether to load song metadata based on the PVs. 
		/// If this is false, only matching for duplicates is done (PV duplicates are still checked).
		/// </param>
		/// <returns>Result of the check. Cannot be null.</returns>
		public NewSongCheckResultContract FindDuplicates(string[] anyName, string[] anyPv, int[] artistIds, bool getPVInfo) {

			var names = anyName.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray();
			var firstNicoPV = getPVInfo ? anyPv.FirstOrDefault(p => VideoService.NicoNicoDouga.IsValidFor(p)) : null; // For downloading video info

			// Parse PV URLs (gets ID and service for each PV). Metadata will be parsed only for the first Nico PV, and only if it's needed.
			var pvs = anyPv.Select(p => pvParser.ParseByUrl(p, getPVInfo && p == firstNicoPV, PermissionContext)).Where(p => p.IsOk).ToArray();

			if (!names.Any() && !pvs.Any())
				return new NewSongCheckResultContract();

			return HandleQuery(ctx => {

				NicoTitleParseResult titleParseResult = null;
				if (getPVInfo) {

					var nicoPV = pvs.FirstOrDefault(p => p.Service == PVService.NicoNicoDouga);

					titleParseResult = ParseNicoPV(ctx.OfType<PVForSong>(), nicoPV);

					if (titleParseResult != null && !string.IsNullOrEmpty(titleParseResult.Title))
						names = names.Concat(new[] { titleParseResult.Title }).ToArray();

					if (titleParseResult != null && titleParseResult.Artists != null && titleParseResult.Artists.Any()) {

						// Append artists from PV
						var pvArtistIds = titleParseResult.Artists.Select(a => a.Id);
						artistIds = artistIds != null ? artistIds.Union(pvArtistIds).ToArray() : pvArtistIds.ToArray();

					}

				}

				var nameMatches = GetNameMatches(ctx, names, artistIds);

				var pvMatches = pvs.Select(pv => ctx.OfType<PVForSong>().Query()
					.Where(p => p.PVId == pv.Id && p.Service == pv.Service)
					.Select(n => n.Song)
					.FirstOrDefault(n => !n.Deleted))
					.Where(p => p != null)
					.Select(d => new Tuple<Song, SongMatchProperty>(d, SongMatchProperty.PV));


				var matches = pvMatches.Union(nameMatches, new SongTupleEqualityComparer<SongMatchProperty>())
					.Select(s => new DuplicateEntryResultContract<SongMatchProperty>(new EntryRefWithCommonPropertiesContract(s.Item1, PermissionContext.LanguagePreference), s.Item2))
					.ToArray();

				return new NewSongCheckResultContract(matches, titleParseResult, PermissionContext.LanguagePreference);

			});

		}

		public RatedSongForUserForApiContract[] GetRatings(int songId, UserOptionalFields userFields, ContentLanguagePreference lang) {

			return repository.HandleQuery(ctx => ctx.Load<Song>(songId).UserFavorites.Select(r => new RatedSongForUserForApiContract(r, userIconFactory, userFields)).ToArray());

		}

		public RelatedSongsContract GetRelatedSongs(int songId, 
			SongOptionalFields fields,
			ContentLanguagePreference? lang = null) {

			var language = lang ?? permissionContext.LanguagePreference;

			return repository.HandleQuery(ctx => {

				var song = ctx.Load(songId);
				var songs = new RelatedSongsQuery(ctx).GetRelatedSongs(song);

				return new RelatedSongsContract {
					ArtistMatches = songs.ArtistMatches
						.Select(a => new SongForApiContract(a, null, language, fields))
						.OrderBy(a => a.Name)
						.ToArray(),
					LikeMatches = songs.LikeMatches
						.Select(a => new SongForApiContract(a, null, language, fields))
						.OrderBy(a => a.Name)
						.ToArray(),
					TagMatches = songs.TagMatches
						.Select(a => new SongForApiContract(a, null, language, fields))
						.OrderBy(a => a.Name)
						.ToArray()
				};
			});

		}

		public void Merge(int sourceId, int targetId) {

			PermissionContext.VerifyPermission(PermissionToken.MergeEntries);

			if (sourceId == targetId)
				throw new ArgumentException("Source and target songs can't be the same", "targetId");

			repository.HandleTransaction(ctx => {

				var source = ctx.Load(sourceId);
				var target = ctx.Load(targetId);

				ctx.AuditLogger.AuditLog(string.Format("Merging {0} to {1}",
					entryLinkFactory.CreateEntryLink(source), entryLinkFactory.CreateEntryLink(target)));

				// Names
				foreach (var n in source.Names.Names.Where(n => !target.HasName(n))) {
					var name = target.CreateName(n.Value, n.Language);
					ctx.Save(name);
				}

				// Weblinks
				foreach (var w in source.WebLinks.Where(w => !target.HasWebLink(w.Url))) {
					var link = target.CreateWebLink(w.Description, w.Url, w.Category);
					ctx.Save(link);
				}

				// PVs
				var pvs = source.PVs.Where(a => !target.HasPV(a.Service, a.PVId));
				foreach (var p in pvs) {
					var pv = target.CreatePV(new PVContract(p));
					ctx.Save(pv);
				}

				// Artist links
				var artists = source.Artists.Where(a => !target.HasArtistLink(a)).ToArray();
				foreach (var a in artists) {
					a.Move(target);
					ctx.Update(a);
				}

				// Album links
				var albums = source.Albums.Where(s => !target.IsOnAlbum(s.Album)).ToArray();
				foreach (var s in albums) {
					s.Move(target);
					ctx.Update(s);
				}

				// Favorites
				var userFavorites = source.UserFavorites.Where(a => !target.IsFavoritedBy(a.User)).ToArray();
				foreach (var u in userFavorites) {
					u.Move(target);
					ctx.Update(u);
				}

				// Custom lists
				var songLists = source.ListLinks.ToArray();
				foreach (var s in songLists) {
					s.ChangeSong(target);
					ctx.Update(s);
				}

				// Other properties
				if (target.OriginalVersion == null)
					target.OriginalVersion = source.OriginalVersion;

				var alternateVersions = source.AlternateVersions.ToArray();
				foreach (var alternate in alternateVersions) {
					alternate.OriginalVersion = target;
					ctx.Update(alternate);
				}

				if (target.LengthSeconds == 0) {
					target.LengthSeconds = source.LengthSeconds;
				}

				target.Notes.CopyIfEmpty(source.Notes);

				if (target.PublishDate.IsEmpty) {
					target.PublishDate = source.PublishDate;
				}

				// Create merge record
				var mergeEntry = new SongMergeRecord(source, target);
				ctx.Save(mergeEntry);

				source.Deleted = true;

				target.UpdateArtistString();
				target.UpdateNicoId();

				Archive(ctx, source, SongArchiveReason.Deleted, string.Format("Merged to {0}", target));
				Archive(ctx, target, SongArchiveReason.Merged, string.Format("Merged from {0}", source));

				ctx.Update(source);
				ctx.Update(target);

			});

		}

		public PVContract PrimaryPVForSong(int songId, PVServices? pvServices) {

			return HandleQuery(ctx => {
				
				var pvs = ctx.Load(songId).PVs.Where(pv => pvServices == null || (pvServices.Value & (PVServices)pv.Service) == (PVServices)pv.Service);
				var primaryPv = VideoServiceHelper.PrimaryPV(pvs, PermissionContext.IsLoggedIn ? (PVService?)PermissionContext.LoggedUser.PreferredVideoService : null);

				return primaryPv != null ? new PVContract(primaryPv) : null;

			});

		}

		public PVContract PVForSong(int pvId) {

			return HandleQuery(session => new PVContract(session.OfType<PVForSong>().Load(pvId)));

		}

		public PVContract PVForSongAndService(int songId, PVService service) {
			
			return HandleQuery(ctx => {
				
				var pvs = ctx.OfType<PVForSong>().Query().Where(pv => pv.Song.Id == songId && pv.Service == service).ToArray();

				var primaryPv = VideoServiceHelper.PrimaryPV(pvs, service);

				return primaryPv != null ? new PVContract(primaryPv) : null;

			});

		}

		public int RemoveTagUsage(long tagUsageId) {

			return new TagUsageQueries(PermissionContext).RemoveTagUsage<SongTagUsage, Song>(tagUsageId, repository);

		}

		public CollectionDiffWithValue<PVForSong, PVForSong> UpdatePVs(IDatabaseContext<Song> ctx, Song song, SongDiff diff, IList<PVContract> pvs) {

			var oldPublishDate = song.PublishDate;
			var pvDiff = song.SyncPVs(pvs);
			ctx.OfType<PVForSong>().Sync(pvDiff);

			if (pvDiff.Changed)
				diff.PVs = true;

			if (pvDiff.Changed && !oldPublishDate.Equals(song.PublishDate)) {
				diff.PublishDate = true;
			}

			return pvDiff;

		}

		public SongForEditContract UpdateBasicProperties(SongForEditContract properties) {

			ParamIs.NotNull(() => properties);

			VerifyManageDatabase();

			return repository.HandleTransaction(ctx => {

				var song = ctx.Load(properties.Id);

				VerifyEntryEdit(song);

				var diff = new SongDiff(DoSnapshot(song.GetLatestVersion(), ctx.OfType<User>().GetLoggedUser(PermissionContext)));

				ctx.AuditLogger.SysLog(string.Format("updating properties for {0}", song));

				var oldPvCount = song.PVs.OfType(PVType.Original).Count();
				diff.Notes = song.Notes.CopyFrom(properties.Notes);

				var newOriginalVersion = (properties.OriginalVersion != null && properties.OriginalVersion.Id != 0 ? ctx.Load(properties.OriginalVersion.Id) : null);

				if (!Equals(song.OriginalVersion, newOriginalVersion)) {
					song.OriginalVersion = newOriginalVersion;
					diff.OriginalVersion = true;
				}

				if (song.SongType != properties.SongType) {
					diff.SongType = true;
					song.SongType = properties.SongType;
				}

				if (song.LengthSeconds != properties.LengthSeconds) {
					diff.Length = true;
					song.LengthSeconds = properties.LengthSeconds;
				}

				if (song.TranslatedName.DefaultLanguage != properties.DefaultNameLanguage) {
					song.TranslatedName.DefaultLanguage = properties.DefaultNameLanguage;
					diff.OriginalName = true;
				}

				var nameDiff = song.Names.Sync(properties.Names, song);
				ctx.OfType<SongName>().Sync(nameDiff);

				if (nameDiff.Changed)
					diff.Names = true;

				var webLinkDiff = WebLink.Sync(song.WebLinks, properties.WebLinks, song);
				ctx.OfType<SongWebLink>().Sync(webLinkDiff);

				if (webLinkDiff.Changed)
					diff.WebLinks = true;

				if (song.Status != properties.Status) {
					song.Status = properties.Status;
					diff.Status = true;
				}

				var artistGetter = new Func<ArtistForSongContract, Artist>(artistForSong => 
					ctx.OfType<Artist>().Load(artistForSong.Artist.Id));

				var artistsDiff = song.SyncArtists(properties.Artists, artistGetter);
				ctx.OfType<ArtistForSong>().Sync(artistsDiff);

				if (artistsDiff.Changed)
					diff.Artists = true;

				var newEvent = new CreateEventQuery().FindOrCreate(ctx, PermissionContext, properties.ReleaseEvent, song);
				if (!song.ReleaseEvent.NullSafeIdEquals(newEvent)) {
					diff.ReleaseEvent = true;
					song.SetReleaseEvent(newEvent);
				}

				if (!song.PublishDate.Equals(properties.PublishDate)) {
					song.PublishDate = properties.PublishDate;
					diff.PublishDate = true;
				}

				UpdatePVs(ctx, song, diff, properties.PVs);

				var lyricsDiff = song.SyncLyrics(properties.Lyrics);
				ctx.OfType<LyricsForSong>().Sync(lyricsDiff);

				if (lyricsDiff.Changed)
					diff.Lyrics = true;

				var logStr = string.Format("updated properties for song {0} ({1})", entryLinkFactory.CreateEntryLink(song), diff.ChangedFieldsString)
					+ (properties.UpdateNotes != string.Empty ? " " + HttpUtility.HtmlEncode(properties.UpdateNotes) : string.Empty)
					.Truncate(400);

				var archived = Archive(ctx, song, diff, SongArchiveReason.PropertiesUpdated, properties.UpdateNotes);
				ctx.Update(song);

				ctx.AuditLogger.AuditLog(logStr);
				AddEntryEditedEntry(ctx.OfType<ActivityEntry>(), song, EntryEditEvent.Updated, archived);

				var newPVCutoff = TimeSpan.FromDays(7);
				if (oldPvCount == 0 && song.PVs.OfType(PVType.Original).Any() && song.CreateDate <= DateTime.Now - newPVCutoff) {
					new FollowedArtistNotifier().SendNotifications(ctx.OfType<UserMessage>(), song, song.ArtistList, PermissionContext.LoggedUser, entryLinkFactory, mailer);
				}

				var newSongCutoff = TimeSpan.FromHours(1);
				if (artistsDiff.Added.Any() && song.CreateDate >= DateTime.Now - newSongCutoff) {

					var addedArtists = artistsDiff.Added.Where(a => a.Artist != null).Select(a => a.Artist).Distinct().ToArray();

					if (addedArtists.Any()) {
						new FollowedArtistNotifier().SendNotifications(ctx.OfType<UserMessage>(), song, addedArtists, PermissionContext.LoggedUser, entryLinkFactory, mailer);											
					}

				}

				return new SongForEditContract(song, PermissionContext.LanguagePreference);

			});

		}

	}

}