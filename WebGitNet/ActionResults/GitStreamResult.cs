﻿//-----------------------------------------------------------------------
// <copyright file="GitStreamResult.cs" company="(none)">
//  Copyright © 2011 John Gietzen. All rights reserved.
// </copyright>
// <author>John Gietzen</author>
//-----------------------------------------------------------------------

namespace WebGitNet.ActionResults
{
    using System;
    using System.IO;
    using System.IO.Compression;
    using System.Threading;
    using System.Web.Mvc;

    public class GitStreamResult : ActionResult
    {
        private readonly string commandFormat;
        private readonly string action;
        private readonly string repoPath;

        public GitStreamResult(string commandFormat, string action, string repoPath)
        {
            if (string.IsNullOrEmpty(commandFormat))
            {
                throw new ArgumentNullException("commandFormat");
            }

            this.commandFormat = commandFormat;

            if (string.IsNullOrEmpty(action))
            {
                throw new ArgumentNullException("action");
            }

            this.action = action;

            if (string.IsNullOrEmpty(repoPath))
            {
                throw new ArgumentNullException("repoPath");
            }

            this.repoPath = repoPath;
        }

        public override void ExecuteResult(ControllerContext context)
        {
            var response = context.HttpContext.Response;
            var request = context.HttpContext.Request;

            response.ContentType = "application/git-" + this.action + "-result";
            response.ContentEncoding = GitUtilities.DefaultEncoding;

            using (var git = GitUtilities.Start(string.Format(this.commandFormat, this.action), this.repoPath, redirectInput: true))
            {
                var readThread = new Thread(() =>
                {
                    var readBuffer = new byte[524288];
                    int readCount;

                    Stream wrapperStream = null;
                    try
                    {
                        var input = request.InputStream;
                        if (request.Headers["Content-Encoding"] == "gzip")
                        {
                            input = wrapperStream = new GZipStream(input, CompressionMode.Decompress);
                        }

                        while ((readCount = input.Read(readBuffer, 0, readBuffer.Length)) > 0)
                        {
                            git.StandardInput.BaseStream.Write(readBuffer, 0, readCount);
                        }
                    }
                    finally
                    {
                        if (wrapperStream != null)
                        {
                            wrapperStream.Dispose();
                        }
                    }

                    git.StandardInput.Close();
                });
                readThread.Start();

                var writeBuffer = new char[4194304];
                int writeCount;
                while ((writeCount = git.StandardOutput.ReadBlock(writeBuffer, 0, writeBuffer.Length)) > 0)
                {
                    response.Write(writeBuffer, 0, writeCount);
                }

                readThread.Join();
                git.WaitForExit();

                if (git.ExitCode != 0)
                {
                    response.StatusCode = 500;
                    response.SubStatusCode = git.ExitCode;
                }
            }
        }
    }
}