﻿using System;
using System.Linq;
using System.Web;
using VocaDb.Model.Database.Repositories;
using VocaDb.Model.DataContracts;
using VocaDb.Model.DataContracts.Users;
using VocaDb.Model.Domain.Comments;
using VocaDb.Model.Domain.Security;
using VocaDb.Model.Domain.Users;
using VocaDb.Model.Service.Helpers;

namespace VocaDb.Model.Service.Queries {

	public interface ICommentQueries {

		CommentForApiContract Create(int entryId, CommentForApiContract contract);

		void Delete(int commentId);

		CommentForApiContract[] GetAll(int entryId);

		int GetCount(int entryId);

		CommentForApiContract[] GetList(int entryId, int count);

		void Update(int commentId, IComment contract);

	}

	public class CommentQueries<T, TEntry> : ICommentQueries where T : GenericComment<TEntry> where TEntry : class, IEntryWithComments {

		private readonly IDatabaseContext<T> ctx;
		private readonly IEntryLinkFactory entryLinkFactory;
		private readonly Func<int, TEntry> entryLoaderFunc;
		private readonly IUserPermissionContext permissionContext;
		private readonly IUserIconFactory userIconFactory;

		private TEntry Load(int entryId) {
			return entryLoaderFunc != null ? entryLoaderFunc(entryId) : ctx.OfType<TEntry>().Load(entryId);
        }

		public CommentQueries(IDatabaseContext<T> ctx, IUserPermissionContext permissionContext, IUserIconFactory userIconFactory, IEntryLinkFactory entryLinkFactory,
			Func<int, TEntry> entryLoaderFunc = null) {

			this.ctx = ctx;
			this.entryLinkFactory = entryLinkFactory;
			this.permissionContext = permissionContext;
			this.userIconFactory = userIconFactory;
			this.entryLoaderFunc = entryLoaderFunc;

		}

		public CommentForApiContract Create(int entryId, CommentForApiContract contract) {

			ParamIs.NotNull(() => contract);

			permissionContext.VerifyPermission(PermissionToken.CreateComments);

			if (contract.Author == null || contract.Author.Id != permissionContext.LoggedUserId) {
				throw new NotAllowedException("Can only post as self");
			}
			
			var entry = Load(entryId);
			var agent = ctx.OfType<User>().CreateAgentLoginData(permissionContext, ctx.OfType<User>().Load(contract.Author.Id));

			var comment = entry.CreateComment(contract.Message, agent);

			ctx.Save(comment);

			ctx.AuditLogger.AuditLog(string.Format("creating comment for {0}: '{1}'", 
				entryLinkFactory.CreateEntryLink(entry), 
				HttpUtility.HtmlEncode(contract.Message)), 
				agent);

			new UserCommentNotifier().CheckComment(comment, entryLinkFactory, ctx.OfType<User>());

			return new CommentForApiContract(comment, userIconFactory);

		}

		public void Delete(int commentId) {
			
			var comment = ctx.OfType<T>().Load(commentId);
			var user = ctx.OfType<User>().GetLoggedUser(permissionContext);

			ctx.AuditLogger.AuditLog("deleting " + comment, user);

			if (!user.Equals(comment.Author))
				permissionContext.VerifyPermission(PermissionToken.DeleteComments);

			comment.OnDelete();
			
			ctx.Delete(comment);

		}

		public CommentForApiContract[] GetAll(int entryId) {

			return Load(entryId)
				.Comments
				.OrderByDescending(c => c.Created)
				.Select(c => new CommentForApiContract(c, userIconFactory))
				.ToArray();

		}

		public int GetCount(int entryId) {
			return ctx.Query<T>().Count(c => c.EntryForComment.Id == entryId);
		}

		public CommentForApiContract[] GetList(int entryId, int count) {

			return ctx.Query<T>().Where(c => c.EntryForComment.Id == entryId)
				.OrderByDescending(c => c.Created).Take(count).ToArray()
				.Select(c => new CommentForApiContract(c, userIconFactory))
				.ToArray();

        }

		public void Update(int commentId, IComment contract) {

			ParamIs.NotNull(() => contract);

			permissionContext.VerifyPermission(PermissionToken.CreateComments);
				
			var comment = ctx.OfType<T>().Load(commentId);

			permissionContext.VerifyAccess(comment, EntryPermissionManager.CanEdit);

			comment.Message = contract.Message;

			ctx.Update(comment);
				
			ctx.AuditLogger.AuditLog(string.Format("updated comment for {0}: '{1}'", 
				entryLinkFactory.CreateEntryLink(comment.Entry), 
				HttpUtility.HtmlEncode(contract.Message)));

		}

	}

}
