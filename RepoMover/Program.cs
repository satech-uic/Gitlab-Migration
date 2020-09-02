using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace RepoMover
{
    class Program
    {
        private static IConfiguration _config;
        private static HttpClient _ghClient, _glClient;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            }
        };

        private static async Task<int> Main(string[] args)
        {
            Setup();

            try
            {
                Console.WriteLine("Creating GitHub repository...");
                var repoName = await GitHubApi.CreateRepository(_config["Github:RepoName"], _config["Github:RepoDescription"]);
                Console.WriteLine($"Created {repoName} on GitHub.");

                Console.WriteLine("Transferring repository from GitLab to GitHub...");
                await TransferRepository(repoName);
                Console.WriteLine($"Transferred {repoName} to GitHub.");

                if (_config.GetSection("Gitlab")["Wiki"] != null)
                {
                    Console.WriteLine($@"You will need to visit the repo's wiki page and create a default page with no content.
This will get overwritten. Here's the link: https://github.com/satech-uic/{repoName}/wiki
Press enter when done.");
                    Console.ReadLine();

                    Console.WriteLine("Transferring wiki...");
                    await TransferWiki(repoName);
                    Console.WriteLine("Wiki transferred");
                }

                Console.WriteLine("Transferring issues...");
                await TransferIssues(repoName);
                Console.WriteLine("Issues transferred.");
            }
            catch(Exception e)
            {
                Console.WriteLine(e.Message);
            }
            
            return 0;
        }

        private static async Task TransferIssues(string githubRepo)
        {
            var issues = await GitlabApi.GetProjectIssues();

            int count = 1, tot = issues.Count;
            foreach (var issue in issues)
            {
                Console.Write($"\rissue {count} of {tot}");
                var issueId = issue["iid"].ToObject<int>();
                var comments = await GitlabApi.GetIssueComments(issueId);

                try
                {
                    // TODO: use the new batch endpoint to add comments to issues on GitHub
                    var githubId = await GitHubApi.CreateIssue(new
                    {
                        Title = issue["title"].Value<string>(),
                        Body =
                            $"{(issue["description"]).Value<string>()}\n\n > Created By: {issue["author"]["name"].Value<string>()}",
                        Labels = issue["labels"]?.ToObject<string[]>()
                    }, githubRepo);

                    foreach (var comment in comments)
                        await GitHubApi.AddCommentToIssue(new
                        {
                            Body = $@"{comment["body"]?.ToObject<string>()}
> Created By: {comment["author"]?["name"]?.ToObject<string>()}
> Created At: {DateTime.Parse(comment["created_at"]?.ToObject<string>() ?? "1970-01-01"):MM/dd/yyyy hh:mm tt}"
                        }, githubRepo, githubId);

                    if (issue["state"]?.ToObject<string>() == "closed") await GitHubApi.CloseIssue(githubRepo, githubId);

                    ++count;
                }
                catch (Exception e)
                {

                    Console.WriteLine(e.Message);
                }
            }
        }

        private static async Task TransferWiki(string githubRepoName)
        {
            using var temp = new TemporaryDirectory();
            using var ps = PowerShell.Create();

            var path = temp.Create();
            var source = _config["Gitlab:Wiki"];
            var repo = source.Substring(
                source.LastIndexOf('/') + 1);

            await CloneToPath(ps, source, path, repo);
            await PushToGithub(ps, $"git@github.com:{_config["Github:OrgName"]}/{githubRepoName}.wiki.git");
            
            await ExecPowershell(ps);
        }

        private static async Task TransferRepository(string githubRepoName)
        {
            using var temp = new TemporaryDirectory();
            using var ps = PowerShell.Create();

            var path = temp.Create();
            var source = _config["Gitlab:Source"];
            var repo = source.Substring(
                source.LastIndexOf('/') + 1);

            await CloneToPath(ps, source, path, repo);
            await PushToGithub(ps, $"git@github.com:{_config["Github:OrgName"]}/{githubRepoName}.git");

            await ExecPowershell(ps);
        }

        // ps should already be at location from Clone To path
        private static async Task PushToGithub(PowerShell ps, string remote)
        {
            Console.WriteLine("Changing origin...");

            ps.AddScript($"git remote set-url origin {remote}").AddStatement();
            ps.AddScript("git push --mirror origin");
        }

        private static async Task CloneToPath(PowerShell ps, string source, string path, string repo)
        {
            Console.WriteLine($"Cloning into {path}...");

            ps.AddCommand("Set-Location").AddParameter("Path", path).AddStatement();
            ps.AddScript($"git clone --mirror {source}").AddStatement();
            ps.AddCommand("Set-Location").AddParameter("Path", repo).AddStatement();
            ps.AddCommand("ls");
        }

        private static async Task ExecPowershell(PowerShell ps)
        {
            var result = await ps.InvokeAsync();

            foreach (var e in ps.Streams.Error) Console.WriteLine($"error: {e}");

            foreach (var x in result) Console.WriteLine($"PS:  {x}");
        }

        private static void Setup()
        {
            //Load Configuration
            _config = new ConfigurationBuilder()
                .SetBasePath(Path.Combine(AppContext.BaseDirectory))
                .AddJsonFile("appsettings.json")
                .AddJsonFile("secrets.json")
                .Build();

            //Setup HTTP Clients

            //github
            _ghClient = new HttpClient
            {
                BaseAddress = new Uri(_config["Github:Url"]),
                DefaultRequestHeaders =
                {
                    Authorization = new AuthenticationHeaderValue("Token", _config["Github:Key"]),
                    UserAgent = {ProductInfoHeaderValue.Parse("idk")}
                }
            };

            //gitlab
            _glClient = new HttpClient(new HttpClientHandler
            {
                AllowAutoRedirect = false
            })
            {
                BaseAddress = new Uri(_config["Gitlab:Url"]),
                DefaultRequestHeaders =
                {
                    Authorization = new AuthenticationHeaderValue("Bearer", _config["Gitlab:Key"])
                }
            };
        }


        private static class GitlabApi
        {
            public static async Task<JArray> GetProjectIssues()
            {
                return await PaginateGitlabRequest($"projects/{_config["Gitlab:ProjectID"]}/issues");
            }

            public static async Task<JObject> GetIssue(int issueId)
            {
                return JObject.Parse(
                    await _glClient.GetStringAsync($"projects/{_config["Gitlab:ProjectID"]}/issues/{issueId}"));
            }

            public static async Task<JArray> GetIssueComments(int issueId)
            {
                return new JArray(
                    (await PaginateGitlabRequest($"projects/{_config["Gitlab:ProjectID"]}/issues/{issueId}/notes"))
                    .OrderBy(i => DateTime.Parse(i["created_at"].ToObject<string>())));
            }

            private static async Task<JArray> PaginateGitlabRequest(string path, string queryString = "?",
                int pageSize = 100)
            {
                var result = new JArray();
                var page = 1;
                while (true)
                {
                    var json = await _glClient.GetStringAsync(
                        $"{path}{queryString}&page={page}&per_page={pageSize}");
                    var x = JArray.Parse(
                        json
                    );

                    if (x.Count == 0) break;

                    result.Merge(x);

                    page++;
                }

                return result;
            }
        }

        private static class GitHubApi
        {
            public class ClientError
            {
                public string resource { get; set; }
                public string field { get; set; }
                public string code { get; set; }
                public string message { get; set; }
            }

            public static async Task<string> CreateRepository(string repoName, string description = "")
            {
                var res = await _ghClient.PostAsync($"/orgs/{_config["Github:OrgName"]}/repos", new StringContent(
                    JsonConvert.SerializeObject(
                        new
                        {
                            Name = repoName,
                            Description = description,
                            Private = true,
                            Visibility = "private",
                            HasIssues = true,
                            HasWiki = true
                        }, JsonSettings), Encoding.Default, "application/json"));

                try
                {
                    res.EnsureSuccessStatusCode();
                    var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                    if (!json["private"].ToObject<bool>()) throw new Exception("Visibility Problem");
                    return json["name"].ToObject<string>();
                }
                catch (HttpRequestException)
                {
                    var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                    var msg = json["message"].ToObject<string>();
                    var errors = json["errors"].ToObject<List<ClientError>>();
                    var sb = new StringBuilder();
                    sb.AppendLine(msg);
                    foreach (var err in errors)
                    {
                        sb.AppendLine($"\t{err.resource}[{err.field}]: {(err.code == "custom" ? err.message : err.code)}");
                    }
                    throw new Exception(sb.ToString());
                }
            }

            public static async Task<int> CreateIssue(object msg, string repo)
            {
                var res = await _ghClient.PostAsync($"/repos/{_config["Github:OrgName"]}/{repo}/issues", new StringContent(
                    JsonConvert.SerializeObject(msg, JsonSettings), Encoding.Default, "application/json"));

                try
                {
                    res.EnsureSuccessStatusCode();
                    var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                    return json["number"].ToObject<int>();
                }
                catch (HttpRequestException)
                {
                    throw new Exception($"Unable to create issue. {res.StatusCode}: {res.Content.ReadAsStringAsync()}");
                }
            }

            public static async Task CloseIssue(string repo, int issueId)
            {
                var res = await _ghClient.PatchAsync($"/repos/{_config["Github:OrgName"]}/{repo}/issues/{issueId}",
                    new StringContent(JsonConvert.SerializeObject(
                        new
                        {
                            State = "closed"
                        }
                        , JsonSettings), Encoding.Default, "application/json"));

                try
                {
                    res.EnsureSuccessStatusCode();
                }
                catch(HttpRequestException)
                {
                    Console.WriteLine($"Unable to close GitHub issue #{issueId}. {res.StatusCode}: {res.Content.ReadAsStringAsync()}");
                }
            }

            public static async Task AddCommentToIssue(object msg, string repo, int issueId)
            {
                var res = await _ghClient.PostAsync($"/repos/{_config["Github:OrgName"]}/{repo}/issues/{issueId}/comments",
                    new StringContent(JsonConvert.SerializeObject(msg, JsonSettings), Encoding.Default,
                        "application/json"));

                try
                {
                    res.EnsureSuccessStatusCode();
                }
                catch(HttpRequestException)
                {
                    Console.WriteLine($"Unable to add comment to GitHub issue #{issueId}. {res.StatusCode}: {res.Content.ReadAsStringAsync()}");
                }
            }
        }

        private class TemporaryDirectory : IDisposable
        {
            private string _path;

            public void Dispose()
            {
                if (string.IsNullOrWhiteSpace(_path)) return;

                var dir = new DirectoryInfo(_path);
                SetAttributesNormal(dir);
                Directory.Delete(_path, true);
            }

            public string Create()
            {
                _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(_path);
                return _path;
            }

            //https://stackoverflow.com/questions/1701457/directory-delete-doesnt-work-access-denied-error-but-under-windows-explorer-it
            private static void SetAttributesNormal(DirectoryInfo dir)
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    SetAttributesNormal(subDir);
                    subDir.Attributes = FileAttributes.Normal;
                }

                foreach (var file in dir.GetFiles()) file.Attributes = FileAttributes.Normal;
            }
        }
    }
}