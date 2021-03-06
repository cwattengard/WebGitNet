﻿//-----------------------------------------------------------------------
// <copyright file="BrowseController.cs" company="(none)">
//  Copyright © 2011 John Gietzen. All rights reserved.
// </copyright>
// <author>John Gietzen</author>
//-----------------------------------------------------------------------

namespace WebGitNet.Controllers
{
    using System.IO;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Web.Mvc;
    using WebGitNet.ActionResults;
    using WebGitNet.Models;

    public class BrowseController : SharedControllerBase
    {
        public BrowseController()
        {
            this.BreadCrumbs.Append("Browse", "Index", "Browse");
        }

        private void AddRepoBreadCrumb(string repo)
        {
            this.BreadCrumbs.Append("Browse", "ViewRepo", repo, new { repo });
        }

        public ActionResult Index()
        {
            var directory = this.FileManager.DirectoryInfo;

            var repos = (from dir in directory.EnumerateDirectories()
                         select dir.Name).ToList();

            return View(repos);
        }

        public ActionResult ViewRepo(string repo)
        {
            var resourceInfo = this.FileManager.GetResourceInfo(repo);
            if (resourceInfo.Type != ResourceType.Directory)
            {
                return HttpNotFound();
            }

            AddRepoBreadCrumb(repo);

            ViewBag.RepoName = resourceInfo.Name;
            ViewBag.LastCommit = GitUtilities.GetLogEntries(resourceInfo.FullPath, 1).FirstOrDefault();
            ViewBag.CurrentTree = GitUtilities.GetTreeInfo(resourceInfo.FullPath, "HEAD");

            return View();
        }

        public ActionResult ViewRepoImpact(string repo)
        {
            var resourceInfo = this.FileManager.GetResourceInfo(repo);
            if (resourceInfo.Type != ResourceType.Directory)
            {
                return HttpNotFound();
            }

            AddRepoBreadCrumb(repo);
            this.BreadCrumbs.Append("Browse", "ViewRepoImpact", "Impact", new { repo });

            var userImpacts = GitUtilities.GetUserImpacts(resourceInfo.FullPath);

            return View(userImpacts);
        }

        public ActionResult ViewCommit(string repo, string @object)
        {
            var resourceInfo = this.FileManager.GetResourceInfo(repo);
            if (resourceInfo.Type != ResourceType.Directory)
            {
                return HttpNotFound();
            }

            AddRepoBreadCrumb(repo);
            this.BreadCrumbs.Append("Browse", "ViewCommit", @object, new { repo, @object });

            var commit = GitUtilities.GetLogEntries(resourceInfo.FullPath, 1, @object).FirstOrDefault();
            if (commit == null)
            {
                return HttpNotFound();
            }

            var diffs = GitUtilities.GetDiffInfo(resourceInfo.FullPath, commit.CommitHash);

            ViewBag.RepoName = resourceInfo.Name;
            ViewBag.CommitLogEntry = commit;

            return View(diffs);
        }

        public ActionResult ViewCommits(string repo)
        {
            var resourceInfo = this.FileManager.GetResourceInfo(repo);
            if (resourceInfo.Type != ResourceType.Directory)
            {
                return HttpNotFound();
            }

            AddRepoBreadCrumb(repo);
            this.BreadCrumbs.Append("Browse", "ViewCommits", "Recent Commits", new { repo });

            var commits = GitUtilities.GetLogEntries(resourceInfo.FullPath, 20);

            ViewBag.RepoName = resourceInfo.Name;

            return View(commits);
        }

        public ActionResult ViewTree(string repo, string @object, string path)
        {
            var resourceInfo = this.FileManager.GetResourceInfo(repo);
            if (resourceInfo.Type != ResourceType.Directory)
            {
                return HttpNotFound();
            }

            AddRepoBreadCrumb(repo);
            this.BreadCrumbs.Append("Browse", "ViewTree", @object, new { repo, @object, path = string.Empty });
            this.BreadCrumbs.Append("Browse", "ViewTree", BreadCrumbTrail.EnumeratePath(path), p => p.Key, p => new { repo, @object, path = p.Value });

            var items = GitUtilities.GetTreeInfo(resourceInfo.FullPath, @object, path);
            ViewBag.RepoName = resourceInfo.Name;
            ViewBag.Tree = @object;
            ViewBag.Path = path ?? string.Empty;

            return View(items);
        }

        public ActionResult ViewBlob(string repo, string @object, string path, bool raw = false)
        {
            var resourceInfo = this.FileManager.GetResourceInfo(repo);
            if (resourceInfo.Type != ResourceType.Directory || string.IsNullOrEmpty(path))
            {
                return HttpNotFound();
            }

            var fileName = Path.GetFileName(path);
            var contentType = MimeUtilities.GetMimeType(fileName);

            if (raw)
            {
                return new GitFileResult(resourceInfo.FullPath, @object, path, contentType);
            }

            AddRepoBreadCrumb(repo);
            this.BreadCrumbs.Append("Browse", "ViewTree", @object, new { repo, @object, path = string.Empty });
            var paths = BreadCrumbTrail.EnumeratePath(path, TrailingSlashBehavior.LeaveOffLastTrailingSlash).ToList();
            this.BreadCrumbs.Append("Browse", "ViewTree", paths.Take(paths.Count() - 1), p => p.Key, p => new { repo, @object, path = p.Value });
            this.BreadCrumbs.Append("Browse", "ViewBlob", paths.Last().Key, new { repo, @object, path = paths.Last().Value });

            ViewBag.RepoName = resourceInfo.Name;
            ViewBag.Tree = @object;
            ViewBag.Path = path;
            ViewBag.FileName = fileName;
            ViewBag.ContentType = contentType;
            string model = null;

            if (contentType.StartsWith("text/") || contentType == "application/xml" || Regex.IsMatch(contentType, @"^application/.*\+xml$"))
            {
                using (var blob = GitUtilities.GetBlob(resourceInfo.FullPath, @object, path))
                {
                    using (var reader = new StreamReader(blob, detectEncodingFromByteOrderMarks: true))
                    {
                        model = reader.ReadToEnd();
                    }
                }
            }

            return View((object)model);
        }
    }
}
