﻿//-----------------------------------------------------------------------
// <copyright file="GitUtilities.cs" company="(none)">
//  Copyright © 2011 John Gietzen. All rights reserved.
// </copyright>
// <author>John Gietzen</author>
//-----------------------------------------------------------------------

namespace WebGitNet
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Web.Configuration;
    using WebGitNet.Models;

    public static class GitUtilities
    {
        public static Encoding DefaultEncoding
        {
            get { return Encoding.GetEncoding(28591); }
        }

        public static string Execute(string command, string workingDir, Encoding outputEncoding = null)
        {
            using (var git = Start(command, workingDir, redirectInput: false, outputEncoding: outputEncoding))
            {
                var result = git.StandardOutput.ReadToEnd();
                git.WaitForExit();
                return result;
            }
        }

        public static Process Start(string command, string workingDir, bool redirectInput = false, Encoding outputEncoding = null)
        {
            var git = WebConfigurationManager.AppSettings["GitCommand"];
            var startInfo = new ProcessStartInfo(git, command)
            {
                WorkingDirectory = workingDir,
                RedirectStandardInput = redirectInput,
                RedirectStandardOutput = true,
                StandardOutputEncoding = outputEncoding ?? DefaultEncoding,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            return Process.Start(startInfo);
        }

        public static void UpdateServerInfo(string repoPath)
        {
            Execute("update-server-info", repoPath);
        }

        public static List<LogEntry> GetLogEntries(string repoPath, int count, string @object = null)
        {
            @object = @object ?? "HEAD";
            var results = Execute(string.Format("log -n {0} --encoding=UTF-8 -z --format=\"format:commit %H%ntree %T%nparent %P%nauthor %an%nauthor mail %ae%nauthor date %aD%ncommitter %cn%ncommitter mail %ce%ncommitter date %cD%nsubject %s%n%b%x00\" {1}", count, @object), repoPath, Encoding.UTF8);

            Func<string, LogEntry> parseResults = result =>
            {
                var commit = ParseResultLine("commit ", result, out result);
                var tree = ParseResultLine("tree ", result, out result);
                var parent = ParseResultLine("parent ", result, out result);
                var author = ParseResultLine("author ", result, out result);
                var authorEmail = ParseResultLine("author mail ", result, out result);
                var authorDate = ParseResultLine("author date ", result, out result);
                var committer = ParseResultLine("committer ", result, out result);
                var committerEmail = ParseResultLine("committer mail ", result, out result);
                var committerDate = ParseResultLine("committer date ", result, out result);
                var subject = ParseResultLine("subject ", result, out result);
                var body = result;

                return new LogEntry(commit, tree, parent, author, authorEmail, authorDate, committer, committerEmail, committerDate, subject, body);
            };

            return (from r in results.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                    select parseResults(r)).ToList();
        }

        public static List<UserImpact> GetUserImpacts(string repoPath)
        {
            string impactData;
            using (var git = Start("log -z --format=format:%an --shortstat", repoPath, outputEncoding: Encoding.UTF8))
            {
                impactData = git.StandardOutput.ReadToEnd();
            }

            var individualImpacts = from imp in impactData.Split('\0')
                                    let lines = imp.Split("\n".ToArray(), StringSplitOptions.RemoveEmptyEntries)
                                    let changeLine = lines.Length > 1 ? lines[1] : ""
                                    let match = Regex.Match(changeLine, @"^ \d+ files changed, (?<insertions>\d+) insertions\(\+\), (?<deletions>\d+) deletions\(-\)$")
                                    let insertions = match.Success ? int.Parse(match.Groups["insertions"].Value) : 0
                                    let deletions = match.Success ? int.Parse(match.Groups["deletions"].Value) : 0
                                    let impact = Math.Max(insertions, deletions)
                                    select new UserImpact
                                    {
                                        Author = lines[0],
                                        Commits = 1,
                                        Insertions = insertions,
                                        Deletions = deletions,
                                        Impact = impact,
                                    };

            return
                individualImpacts
                .GroupBy(i => i.Author, StringComparer.InvariantCultureIgnoreCase)
                .Select(g => new UserImpact
                {
                    Author = g.Key,
                    Commits = g.Sum(ui => ui.Commits),
                    Insertions = g.Sum(ui => ui.Insertions),
                    Deletions = g.Sum(ui => ui.Deletions),
                    Impact = g.Sum(ui => ui.Impact),
                })
                .ToList();
        }

        public static List<DiffInfo> GetDiffInfo(string repoPath, string commit)
        {
            var diffs = new List<DiffInfo>();
            List<string> diffLines = null;

            Action addLastDiff = () =>
            {
                if (diffLines != null)
                {
                    diffs.Add(new DiffInfo(diffLines));
                }
            };

            using (var git = Start(string.Format("diff-tree -p -c -r {0}", commit), repoPath))
            {
                while (!git.StandardOutput.EndOfStream)
                {
                    var line = git.StandardOutput.ReadLine();

                    if (diffLines == null && !line.StartsWith("diff"))
                    {
                        continue;
                    }

                    if (line.StartsWith("diff"))
                    {
                        addLastDiff();
                        diffLines = new List<string> { line };
                    }
                    else
                    {
                        diffLines.Add(line);
                    }
                }
            }

            addLastDiff();

            return diffs;
        }

        public static TreeView GetTreeInfo(string repoPath, string tree, string path = null)
        {
            if (string.IsNullOrEmpty(tree))
            {
                throw new ArgumentNullException("tree");
            }

            if (!Regex.IsMatch(tree, "^[-a-zA-Z0-9]+$"))
            {
                throw new ArgumentOutOfRangeException("tree", "tree mush be the id of a tree-ish object.");
            }

            path = path ?? string.Empty;
            path = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var results = Execute(string.Format("ls-tree -l -z {0}:\"{1}\"", tree, path), repoPath, Encoding.UTF8);

            if (results.StartsWith("fatal: "))
            {
                throw new Exception(results);
            }

            Func<string, ObjectInfo> parseResults = result =>
            {
                var mode = ParseTreePart(result, "[ ]+", out result);
                var type = ParseTreePart(result, "[ ]+", out result);
                var hash = ParseTreePart(result, "[ ]+", out result);
                var size = ParseTreePart(result, "\\t+", out result);
                var name = result;

                return new ObjectInfo(
                    (ObjectType)Enum.Parse(typeof(ObjectType), type, ignoreCase: true),
                    hash,
                    size == "-" ? (int?)null : int.Parse(size),
                    name);
            };

            var objects = from r in results.Split(new[] { '\0' }, StringSplitOptions.RemoveEmptyEntries)
                          select parseResults(r);
            return new TreeView(tree, path, objects);
        }

        public static Process StartGetBlob(string repoPath, string tree, string path)
        {
            if (string.IsNullOrEmpty(tree))
            {
                throw new ArgumentNullException("tree");
            }

            if (string.IsNullOrEmpty(path))
            {
                throw new ArgumentNullException("path");
            }

            if (!Regex.IsMatch(tree, "^[-a-zA-Z0-9]+$"))
            {
                throw new ArgumentOutOfRangeException("tree", "tree mush be the id of a tree-ish object.");
            }

            path = path.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return Start(string.Format("show {0}:\"{1}\"", tree, path), repoPath, redirectInput: false);
        }

        public static MemoryStream GetBlob(string repoPath, string tree, string path)
        {
            MemoryStream blob = null;
            try
            {
                blob = new MemoryStream();
                using (var git = StartGetBlob(repoPath, tree, path))
                {
                    var buffer = new byte[1048576];
                    var readCount = 0;
                    while ((readCount = git.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        blob.Write(buffer, 0, readCount);
                    }
                }

                blob.Seek(0, SeekOrigin.Begin);

                var tempBlob = blob;
                blob = null;
                return tempBlob;
            }
            finally
            {
                if (blob != null)
                {
                    blob.Dispose();
                }
            }
        }

        public static void CreateRepo(string repoPath)
        {
            var workingDir = Path.GetDirectoryName(repoPath);
            var newPath = repoPath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var results = Execute(string.Format("init --bare \"{0}\"", newPath), workingDir);

            var errorLines = results.Split('\n').Where(l => l.StartsWith("fatal:")).ToList();
            if (errorLines.Count > 0)
            {
                throw new CreateRepoFailedException(string.Join(results, Environment.NewLine));
            }
        }

        public static void ExecutePostCreateHook(string repoPath)
        {
            var sh = WebConfigurationManager.AppSettings["ShCommand"];

            // If 'sh.exe' is not configured, derive the path relative to the git.exe command path.
            if (string.IsNullOrEmpty(sh))
            {
                var git = WebConfigurationManager.AppSettings["GitCommand"];
                sh = Path.Combine(Path.GetDirectoryName(git), "sh.exe");
            }

            // Find the path of the post-create hook.
            var repositories = WebConfigurationManager.AppSettings["RepositoriesPath"];
            var hookRelativePath = WebConfigurationManager.AppSettings["PostCreateHook"];

            // If the hook path is not configured, default to a path of "post-create", relative to the repository directory.
            if (string.IsNullOrEmpty(hookRelativePath))
            {
                hookRelativePath = "post-create";
            }

            // Get the full path info for the hook file, and ensure that it exists.
            var hookFile = new FileInfo(Path.Combine(repositories, hookRelativePath));
            if (!hookFile.Exists)
            {
                return;
            }

            // Prepare to start sh.exe like: `sh.exe -- "C:\Path\To\Hook-Script"`.
            var startInfo = new ProcessStartInfo(sh, string.Format("-- \"{0}\"", hookFile.FullName.Replace("\\", "\\\\").Replace("\"", "\\\"")))
            {
                WorkingDirectory = repoPath,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            startInfo.EnvironmentVariables["PATH"] = Environment.GetEnvironmentVariable("PATH") + Path.PathSeparator + Path.GetDirectoryName(sh);

            // Start the script and wait for exit.
            using (var script = Process.Start(startInfo))
            {
                script.WaitForExit();
            }
        }

        private static string ParseResultLine(string prefix, string result, out string rest)
        {
            var parts = result.Split(new[] { '\n' }, 2);
            rest = parts[1];
            return parts[0].Substring(prefix.Length);
        }

        private static string ParseTreePart(string result, string delimiterPattern, out string rest)
        {
            var match = Regex.Match(result, delimiterPattern);

            if (!match.Success)
            {
                rest = result;
                return null;
            }
            else
            {
                rest = result.Substring(match.Index + match.Length);
                return result.Substring(0, match.Index);
            }
        }

        [global::System.Serializable]
        public class CreateRepoFailedException : Exception
        {
            public CreateRepoFailedException()
            {
            }

            public CreateRepoFailedException(string message)
                : base(message)
            {
            }

            public CreateRepoFailedException(string message, Exception inner)
                : base(message, inner)
            {
            }

            protected CreateRepoFailedException(System.Runtime.Serialization.SerializationInfo info, System.Runtime.Serialization.StreamingContext context)
                : base(info, context)
            {
            }
        }
    }
}
