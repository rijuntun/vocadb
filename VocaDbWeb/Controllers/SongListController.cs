﻿using System.Net;
using System.Web.Mvc;
using VocaDb.Model.DataContracts;
using VocaDb.Model.DataContracts.Songs;
using VocaDb.Model.Service;
using VocaDb.Model.Service.Rankings;
using VocaDb.Model.Utils;
using VocaDb.Web.Controllers.DataAccess;
using VocaDb.Web.Models.SongLists;

namespace VocaDb.Web.Controllers
{
    public class SongListController : ControllerBase
    {

		public const int SongsPerPage = 50;
		private readonly SongListQueries queries;
		private readonly RankingService rankingService;
	    private readonly SongService service;

		private SongService Service {
			get { return service; }
		}

		public SongListController(SongService service, RankingService rankingService, SongListQueries queries) {
			this.service = service;
			this.rankingService = rankingService;
			this.queries = queries;
		}

		public ActionResult CreateFromWVR() {

			var model = new CreateFromWVR();
			return View(model);

		}

		[HttpPost]
		public ActionResult CreateFromWVR(CreateFromWVR model, bool commit) {

			if (commit) {
				var listId = rankingService.CreateSongListFromWVR(model.Url, model.ParseAll);
				return RedirectToAction("Details", "SongList", new { id = listId });
			}

			WVRListResult parseResult;
			try {
				parseResult = rankingService.ParseWVRList(model.Url, model.ParseAll);
			} catch (InvalidFeedException) {
				ModelState.AddModelError("Url", "Check that the URL is valid and points to a NicoNico MyList or RSS feed.");
				return View(model);
			}
			model.ListResult = parseResult;

			if (parseResult.IsIncomplete)
				ModelState.AddModelError("ListResult", "Some of the songs are missing from VocaDB. You need to add all songs to the database before creating the list.");

			return View(model);

		}

		public ActionResult Delete(int id) {

			Service.DeleteSongList(id);

			return RedirectToAction("Profile", "User", new { id = PermissionContext.LoggedUser.Name });

		}

		public ActionResult Details(int id = invalidId) {

			if (id == invalidId)
				return NoId();

			var contract = queries.GetSongList(id);

			PageProperties.CanonicalUrl = VocaUriBuilder.CreateAbsolute(Url.Action("Details", new { id })).ToString();
			PageProperties.Description = contract.Description;

			return View(contract);

		}

        //
        // GET: /SongList/Edit/
        [Authorize]
        public ActionResult Edit(int? id)
        {

			var contract = id != null ? queries.GetSongList(id.Value) : new SongListContract();
			var model = new SongListEditViewModel(contract, PermissionContext);

            return View(model);

        }

		[HttpPost]
        [Authorize]
		public ActionResult Edit(SongListEditViewModel model)
        {

			if (model == null) {
				return HttpStatusCodeResult(HttpStatusCode.BadRequest, "View model was null - probably JavaScript is disabled");				
			}

			var coverPicUpload = Request.Files["thumbPicUpload"];
			UploadedFileContract uploadedPicture = null;
			if (coverPicUpload != null && coverPicUpload.ContentLength > 0) {

				CheckUploadedPicture(coverPicUpload, "thumbPicUpload");
				uploadedPicture = new UploadedFileContract {Mime = coverPicUpload.ContentType, Stream = coverPicUpload.InputStream};

			}

			if (!ModelState.IsValid) {
				return View(new SongListEditViewModel(model.ToContract(), PermissionContext));
			}

			var listId = queries.UpdateSongList(model.ToContract(), uploadedPicture);

			return RedirectToAction("Details", new { id = listId });

		}

		public ActionResult Featured() {

			var lists = Service.GetSongListsByCategory();

			return View(lists);

		}

		public ActionResult Versions(int id = invalidId) {

			if (id == invalidId)
				return NoId();

			var contract = queries.GetSongListWithArchivedVersions(id);

			if (contract == null)
				return HttpNotFound();

			return View(contract);

		}
    }
}
