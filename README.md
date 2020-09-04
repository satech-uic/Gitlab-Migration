# GitLab to GitHub Repo Migration Script

This C# .NET Core script migrates GitLab repos to GitHub using HTTP APIs. It also migrates issues and wikis associated with the GitLab project.

## Installation

The script requires PowerShell and git. Run on a Windows computer and ensure git is added to the `PATH` environment variable.

`git clone https://github.com/satech-uic/Gitlab-Migration`

Open cloned repo using Visual Studio or Rider.

The solution targets .NET Core 3.1.

## Usage

The script looks for configuration in `appsettings.json` and `sercrets.json`. Create the `secrets.json` file using the example file `secrents.json.example.json`.

Pass in authentication tokens for both APIs using `secrets.json`. You will need to create API tokens with **read/write permissions** to transfer the repo and other data.

Run the script with the desiered parameters. You will be asked to create the repo's GitHub wiki manually. After execution, the script will report the number of issues transferred.

### Issue Transfer

Migrated issues and comments will be created by the user who created the API tokens. As a workaround, the script inserts a footer to each issue indicating the original author and created date. See example.

> Created By: ccunni3

> Created On: 09/04/2020 12:34 PM

## Support

Please file an issue with the repository for bugs or changes. Otherwise use the SA Technology Service Desk portal to raise a request.
