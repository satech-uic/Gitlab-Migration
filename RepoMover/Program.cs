using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using Newtonsoft.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace RepoMover
{
    class Program
    {
        private static IConfiguration _config;
        private static HttpClient _ghClient, _glClient;

        private static JsonSerializerSettings _jsonSettings = new JsonSerializerSettings
        {
            // Formatting = Formatting.Indented,
            ContractResolver = new DefaultContractResolver
            {
                NamingStrategy = new SnakeCaseNamingStrategy()
            }
        };

        // todos (Dhyan)
        // Gitlab - https://docs.gitlab.com/ee/api/README.html
        // Github - https://developer.github.com/v3/
        // Create Github repo through the api and get the repository name (Done)
        // Push to github from temp file (Done)
        // Transfer wikis using the same method as repos (Done)
        // Investigate rate limiting for issue creation.

        // Create wiki page for merge request comments

        static async Task<int> Main(string[] args)
        {
            Setup();

            Console.WriteLine("Creating Repo on Github...");
            var repoName = await GitHubApi.CreateRepository(
                _config["Github:RepoName"], 
                _config["Github:RepoDescription"]);
            
            Console.WriteLine("Transferring Repo...");
            await TransferRepository(repoName);
            
            if (_config.GetSection("Gitlab")["Wiki"] != null)
            {
                Console.WriteLine(
                    "You will need to visit the repo's wiki page and create a default page with no content." +
                    "This will get overwritten. Here's the link:\n"+
                    $"https://github.com/satech-uic/{repoName}/wiki\n"+
                    "Press enter when done.");
                Console.ReadLine();

                Console.WriteLine("Transferring Wiki...");
                await TransferWiki(repoName);
            }
            
            Console.WriteLine("Transferring issues...");
            await TransferIssues(repoName);
            
            

            

            // var repoName = await GitHubApi.CreateRepository("test-repo2", "This is a test repository!");


            // await TransferIssues("lrs-api");


            return 0;
        }

        private static async Task TransferIssues(string githubRepo)
        {
            var issues = await GitlabApi.GetProjectIssues();
            // var issues = new JArray(await GitlabApi.GetIssue(80));

            int count = 1, tot = issues.Count;
            foreach (var issue in issues)
            {
                Console.Write($"\rissue {count} of {tot}");
                var issueId = issue["iid"].ToObject<int>();
                var comments = await GitlabApi.GetIssueComments(issueId);

                var githubId = await GitHubApi.CreateIssue(new
                {
                    Title = issue["title"].Value<string>(),
                    Body =
                        $"{issue["description"].Value<string>()}\n\n > Created By: {issue["author"]["name"].Value<string>()}",
                    Labels = issue["labels"].ToObject<string[]>()
                }, githubRepo);

                foreach (var comment in comments)
                {
                    await GitHubApi.AddCommentToIssue(new
                    {
                        Body = $@"{comment["body"].ToObject<string>()}

> Created By: {comment["author"]["name"].ToObject<string>()}
> Created At: {DateTime.Parse(comment["created_at"].ToObject<string>()):MM/dd/yyyy hh:mm tt}"
                    }, githubRepo, githubId);
                }

                if (issue["state"].ToObject<string>() == "closed")
                {
                    await GitHubApi.CloseIssue(githubRepo, githubId);
                }

                ++count;
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
            await PushToGithub(ps, $"git@github.com:satech-uic/{githubRepoName}.wiki.git");
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
            await PushToGithub(ps, $"git@github.com:satech-uic/{githubRepoName}.git");

            await ExecPowershell(ps);
            
            //todo push to github

            Console.WriteLine("Done");
        }

        //ps should already be at location from Clone To path
        private static async Task PushToGithub(PowerShell ps, string remote)
        {
            Console.WriteLine($"Changing origin...");

            ps.AddScript($"git remote set-url origin {remote}").AddStatement();
            ps.AddScript($"git push --mirror origin");
            
        }

        private static async Task CloneToPath(PowerShell ps, string source, string path, string repo)
        {
            Console.WriteLine($"Cloning into {path}...");

            ps.AddCommand("Set-Location").AddParameter("Path", path).AddStatement();
            ps.AddScript($"git clone --mirror {source}").AddStatement();
            ps.AddCommand("Set-Location").AddParameter("Path", repo).AddStatement();
            ps.AddCommand("ls");

            // await ExecPowershell(ps);
            // ps.
        }

        private static async Task ExecPowershell(PowerShell ps)
        {
            var result = await ps.InvokeAsync();

            foreach (var e in ps.Streams.Error)
            {
                Console.WriteLine($"error: {e}");
            }

            foreach (var x in result)
            {
                Console.WriteLine($"PS:  {x}");
            }
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
            _ghClient = new HttpClient()
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

                    if (x.Count == 0)
                    {
                        break;
                    }

                    result.Merge(x);

                    page++;
                }

                return result;
            }
        }

        private static class GitHubApi
        {
            public static async Task<string> CreateRepository(string repoName, string description = "")
            {
                var res = await _ghClient.PostAsync($"/orgs/satech-uic/repos", new StringContent(
                    JsonConvert.SerializeObject(
                        new
                        {
                            Name = repoName,
                            Description = description,
                            Private = true,
                            Visibility = "private",
                            HasIssues = true,
                            HasWiki = true
                        }, _jsonSettings), Encoding.Default, "application/json"));

                res.EnsureSuccessStatusCode();
                var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                if (!json["private"].ToObject<bool>())
                {
                    throw new Exception("Visibility Problem");
                }

                return json["name"].ToObject<string>();
            }

            public static async Task<int> CreateIssue(object msg, string repo)
            {
                var res = await _ghClient.PostAsync($"/repos/satech-uic/{repo}/issues", new StringContent(
                    JsonConvert.SerializeObject(msg, _jsonSettings), Encoding.Default, "application/json"));

                try
                {
                    res.EnsureSuccessStatusCode();
                    var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                    return json["number"].ToObject<int>();
                }
                catch (HttpRequestException)
                {
                    Console.WriteLine(res.Content.ReadAsStringAsync());
                    throw;
                }
            }

            public static async Task CloseIssue(string repo, int issueId)
            {
                var res = await _ghClient.PatchAsync($"/repos/satech-uic/{repo}/issues/{issueId}",
                    new StringContent(JsonConvert.SerializeObject(
                        new
                        {
                            State = "closed"
                        }
                        , _jsonSettings), Encoding.Default, "application/json"));

                res.EnsureSuccessStatusCode();
            }

            public static async Task<int> AddCommentToIssue(object msg, string repo, int issueId)
            {
                var res = await _ghClient.PostAsync($"/repos/satech-uic/{repo}/issues/{issueId}/comments",
                    new StringContent(JsonConvert.SerializeObject(msg, _jsonSettings), Encoding.Default,
                        "application/json"));

                res.EnsureSuccessStatusCode();
                var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                return json["id"].ToObject<int>();
            }
        }

        private class TemporaryDirectory : IDisposable
        {
            private string _path;

            public string Create()
            {
                _path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
                Directory.CreateDirectory(_path);
                return _path;
            }

            public void Dispose()
            {
                if (string.IsNullOrWhiteSpace(_path)) return;

                var dir = new DirectoryInfo(_path);
                SetAttributesNormal(dir);
                Directory.Delete(_path, recursive: true);
            }

            //https://stackoverflow.com/questions/1701457/directory-delete-doesnt-work-access-denied-error-but-under-windows-explorer-it
            private static void SetAttributesNormal(DirectoryInfo dir)
            {
                foreach (var subDir in dir.GetDirectories())
                {
                    SetAttributesNormal(subDir);
                    subDir.Attributes = FileAttributes.Normal;
                }

                foreach (var file in dir.GetFiles())
                {
                    file.Attributes = FileAttributes.Normal;
                }
            }
        }
    }
}