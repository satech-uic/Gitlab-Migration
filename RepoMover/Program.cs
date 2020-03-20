using System;
using System.IO;
using System.Linq;
using System.Management.Automation;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Newtonsoft.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Operations;
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
        // Github - https://developer.github.com/v4/
        // Create Github repo through the api and get the repository name
        // Push to github from temp file
        
        // Create wiki page for merge request comments

        static async Task<int> Main(string[] args)
        {
            Setup();
            // await CloneRepository();

            // var res = await _glClient.GetStringAsync("https://sa-vm-gitlab.cc.uic.edu/api/v4/projects");
            // var res = await PaginateGitlabRequest($"projects/{_config["Gitlab:ProjectID"]}/issues");
            // var res = await GitlabApi.GetProjectIssues();
            // Console.WriteLine(res.Count);
            // Console.WriteLine(res.FirstOrDefault());

            // var issue = res.First();
            
            // var issue = await GitlabApi.GetIssue(71);
            // var comments = await GitlabApi.GetIssueComments(issue["iid"].ToObject<int>());
            // var x = await GitlabApi.GetIssueComments(71);
            // var sorted = new JArray(x.OrderBy(i=>DateTime.Parse(i["created_at"].ToObject<string>())));
            // Console.WriteLine(sorted);
            
            // var githubIssue = await GitHubApi.CreateIssue(
            // new {
            //     Title = "Test Issue",
            //     Body = "# Markdown\nDon't mind me, just putting an @jterha2 and some *snazzy* **markdown**. Oh, and here's a [link](https://www.google.com).",
            //     // Body = "test",
            //     Labels = new[]{"Non Existent Label", "Emoji Label 🐛"}
            //     // Assignees = new [] {"jterha2", "ccunni3@uic.edu", "idk"}
            // }, "lrs-api");
            
            // Console.WriteLine(githubIssue);

            await TransferIssues("lrs-api");
            

            // _ghClient.PostAsync("/repos/satech/lrs-api/issues", new StringContent(
            //     JsonConvert.SerializeObject(new
            //     {
            //         Title = issue["title"].Value<string>(),
            //         Body = $"{issue["description"]}<br/><br/>Created By: {issue["author"]["name"].Value<string>()}",
            //         Labels = (issue["labels"] as JArray)?.ToObject<string[]>(),
            //         Assignees = (issue["assignees"] as JArray)?.ToObject<string[]>()
            //     })
            //     ,
            //     Encoding.Unicode, "application/json"));


            // Console.WriteLine(JsonConvert.SerializeObject(x));


            // var res = await _client.GetAsync("/user");


            // var res = await _client.GetStringAsync("/repos/satech-uic/lrs-api");
            // var x = JObject.Parse(res);
            // Console.WriteLine(x["id"].ToString());
            // Console.WriteLine(JsonConvert.SerializeObject(
            //     new
            //     {
            //         Name = "Repository Name",
            //         Description = "This is the description",
            //         Visibility = "private",
            //         HasIssues = true,
            //         HasWiki = true
            //     }, _jsonSettings));

            // Console.WriteLine(res);
            // var dir = CreateTempDirectory();
            // Console.WriteLine(dir);

            // var s = JsonSerializer.Serialize(new
            // {
            //     Name = "Repository Name",
            //     Description = "This is the description",
            //     Visibility = "private",
            //     HasIssues = true,
            //     HasWiki = true
            // }, new JsonSerializerOptions()
            // {
            //     WriteIndented = true,
            //     PropertyNamingPolicy = new GithubJsonPolicy()
            // });

            // Console.WriteLine(s);

            return 0;
        }

        private static async Task TransferIssues(string githubRepo)
        {
            // var issues = await GitlabApi.GetProjectIssues();
            var issues = new JArray(await GitlabApi.GetIssue(80));

            foreach (var issue in issues)
            {
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
            }
        }

        private static async Task CloneRepository()
        {
            using var temp = new TemporaryDirectory();


            var path = temp.Create();
            var source = _config["Gitlab:Source"];
            var repo = source.Substring(
                source.LastIndexOf('/') + 1);

            await CloneToPath(source, path, repo);
            
            //todo push to github
            
            Console.WriteLine("Done");
        }

        private static async Task CloneToPath(string source, string path, string repo)
        {
            using var ps = PowerShell.Create();


            Console.WriteLine($"Cloning into {path}...");

            ps.AddCommand("Set-Location").AddParameter("Path", path).AddStatement();
            ps.AddScript($"git clone --mirror {source}").AddStatement();
            ps.AddCommand("Set-Location").AddParameter("Path", repo).AddStatement();
            ps.AddCommand("ls");

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
                        .OrderBy(i=>DateTime.Parse(i["created_at"].ToObject<string>())));
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
            public static async Task<int> CreateIssue(object msg, string repo)
            {
                var res = await _ghClient.PostAsync($"/repos/satech-uic/{repo}/issues", new StringContent(
                    JsonConvert.SerializeObject(msg, _jsonSettings), Encoding.Default, "application/json"));

                res.EnsureSuccessStatusCode();
                var json = JObject.Parse(await res.Content.ReadAsStringAsync());
                return json["number"].ToObject<int>();
            }

            public static async Task CloseIssue(string repo, int issueId)
            {
                var res = await _ghClient.PatchAsync($"/repos/satech-uic/{repo}/issues/{issueId}",
                    new StringContent(JsonConvert.SerializeObject(
                        new
                        {
                            State="closed"
                        }
                        , _jsonSettings), Encoding.Default, "application/json"));
                
                res.EnsureSuccessStatusCode();
            }

            public static async Task<int> AddCommentToIssue(object msg, string repo, int issueId)
            {
                var res = await _ghClient.PostAsync($"/repos/satech-uic/{repo}/issues/{issueId}/comments",
                    new StringContent(JsonConvert.SerializeObject(msg, _jsonSettings), Encoding.Default, "application/json"));

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