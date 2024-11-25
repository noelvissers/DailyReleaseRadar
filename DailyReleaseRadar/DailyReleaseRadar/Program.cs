using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using SpotifyAPI.Web;
using SpotifyAPI.Web.Http;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using static SpotifyAPI.Web.PlaylistRemoveItemsRequest;

namespace DailyReleaseRadar
{
  internal class Program
  {
    static void Main(string[] args)
    {
      Console.ForegroundColor = ConsoleColor.White;
      Console.WriteLine("[Starting Daily Release Radar]");
      try
      {
        DailyReleaseRadar dailyReleaseRadar = new();
        dailyReleaseRadar.Run().GetAwaiter().GetResult();
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(ex.Message);
        Console.WriteLine(ex.StackTrace);
        Console.ResetColor();
      }
    }
  }

  public class DailyReleaseRadar
  {
    private SpotifyClient? spotify;

    public async Task<bool> Run()
    {
      await Authenticate();

      // Get tracks currently in playlist
      List<PlaylistTrack<IPlayableItem>> playlistTracks = await GetPlaylistTracks();

      // Remove tracks older than 7 days
      await RemoveOldTracksFromPlaylist(playlistTracks);

      //Get list of artist user is following
      List<FullArtist> followingArtists = await GetArtistsFollowing();

      // Get list of today's releases from artist list
      List<FullTrack> tracksToAdd = await GetTodaysReleasesFromArtists(followingArtists);

      // Update tracks currently in playlist
      playlistTracks = await GetPlaylistTracks();

      // Add uniqueTracksToPlaylist
      await AddUniqueTracksToPlaylist(tracksToAdd, playlistTracks);

      return true;
    }

    public async Task Authenticate()
    {
      var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddUserSecrets<Program>()
        .Build();

      var clientId = configurationBuilder.GetSection("Spotify:ClientId").Value;
      var clientSecret = configurationBuilder.GetSection("Spotify:ClientSecret").Value;
      var redirectUri = new Uri(configurationBuilder.GetSection("Spotify:RedirectUri").Value);
      var refreshToken = configurationBuilder.GetSection("Spotify:RefreshToken").Value;

      try
      {
        Console.WriteLine("[AUTH] Trying to login via refresh token...");
        AuthorizationCodeRefreshResponse response = await new OAuthClient().RequestToken(new AuthorizationCodeRefreshRequest(clientId, clientSecret, refreshToken));
        spotify = new SpotifyClient(response.AccessToken);

        Console.WriteLine($"[AUTH] Access Token: {response.AccessToken}");
        Console.WriteLine($"[AUTH] Refresh Token: {refreshToken}");
      }
      catch (Exception)
      {
        Console.WriteLine("[AUTH] Trying to login via new request...");

        var request = new LoginRequest(redirectUri, clientId, LoginRequest.ResponseType.Code)
        {
          Scope = [Scopes.UserFollowRead, Scopes.UserReadPrivate, Scopes.PlaylistReadPrivate, Scopes.PlaylistModifyPrivate, Scopes.PlaylistModifyPublic] // Include necessary scopes
        };

        var uri = request.ToUri();
        Console.WriteLine($"[AUTH] Please navigate to {uri} and log in.");
        Console.Write("[AUTH] Enter the code from the redirect URI: ");
        var code = Console.ReadLine();
        var response = await new OAuthClient().RequestToken(new AuthorizationCodeTokenRequest(clientId, clientSecret, code, redirectUri));

        spotify = new SpotifyClient(response.AccessToken);

        Console.WriteLine($"[AUTH] Access Token: {response.AccessToken}");
        Console.WriteLine($"[AUTH] Refresh Token: {response.RefreshToken}");
      }
    }

    public async Task<List<FullArtist>> GetArtistsFollowing()
    {
      var allArtists = new List<FullArtist>();

      try
      {
        string after = string.Empty;
        int limit = 50;

        do
        {
          var followedArtists = await spotify.Follow.OfCurrentUser(new FollowOfCurrentUserRequest { After = after, Limit = limit });
          allArtists.AddRange(followedArtists.Artists.Items);
          after = followedArtists.Artists.Cursors.After;
        } while (after != null);
        /*
        foreach (var artist in allArtists)
        {
          Console.WriteLine($"[{artist.Id}] {artist.Name}");
        }
        */
      }
      catch (APIException ex)
      {
        Console.WriteLine($"Error: {ex.Message}");
      }

      return allArtists;
    }

    public async Task<List<FullTrack>> GetTodaysReleasesFromArtists(List<FullArtist> artists)
    {
      List<FullTrack> tempTracksToAdd = new();
      DateTime today = DateTime.UtcNow.Date;

      // Handle rate limit
      Stopwatch stopwatch = Stopwatch.StartNew();

      int requests = 0;
      int i = 0;
      foreach (FullArtist artist in artists)
      {
        i++;
        Console.WriteLine($"[{i}/{artists.Count()}] [{artist.Id}] {artist.Name}:");

        // Handle rate limit
        stopwatch.Restart();
        requests = 0;

        // Get latest album
        try
        {
          var albumsRequest = new ArtistsAlbumsRequest
          {
            Limit = 10,
            IncludeGroupsParam = ArtistsAlbumsRequest.IncludeGroups.Album
          };

          var albumsResponse = await spotify.Artists.GetAlbums(artist.Id, albumsRequest);
          requests++;

          var latestAlbum = albumsResponse.Items
            .Select(album => new
            {
              Album = album,
              ReleaseDate = DateTime.TryParseExact(
                album.ReleaseDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date) ? date : (DateTime?)null
            })
            .Where(item => item.ReleaseDate.HasValue)
            .OrderByDescending(item => item.ReleaseDate)
            .FirstOrDefault();

          if (latestAlbum != null)
          {
            if (((DateTime)latestAlbum.ReleaseDate).Date == today)
            {
              Console.ForegroundColor = ConsoleColor.Blue;
              Console.WriteLine($"  Latest album: [{latestAlbum.ReleaseDate.Value.ToShortDateString()}] {latestAlbum.Album.Name}");

              var album = await spotify.Albums.Get(latestAlbum.Album.Id);
              requests++;

              foreach (var track in album.Tracks.Items)
              {
                var fullTrack = await spotify.Tracks.Get(track.Id);
                requests++;
                if (!tempTracksToAdd.Any(item => item.ExternalIds.First().Value == fullTrack.ExternalIds.First().Value))
                {
                  tempTracksToAdd.Add(fullTrack);
                  Console.ForegroundColor = ConsoleColor.Green;
                  Console.Write($"    Added: [{track.TrackNumber}] ");
                }
                else
                {
                  Console.ForegroundColor = ConsoleColor.Yellow;
                  Console.Write($"    Skipped: [{track.TrackNumber}] ");
                }
                foreach (var trackArtist in track.Artists)
                  Console.Write($"{trackArtist.Name} ");
                Console.WriteLine($"- {track.Name}");
              }
              Console.ResetColor();
            }
            else
              Console.WriteLine($"  Latest album: [{latestAlbum.ReleaseDate.Value.ToShortDateString()}] {latestAlbum.Album.Name}");
          }
          else
            Console.WriteLine($"  No albums found for artist.");
        }
        catch (APIException ex)
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"Error: {ex.Message}");
          Console.ResetColor();
        }

        // Get latest single (this also includes EPs)
        try
        {
          var albumsRequest = new ArtistsAlbumsRequest
          {
            Limit = 10,
            IncludeGroupsParam = ArtistsAlbumsRequest.IncludeGroups.Single
          };

          var albumsResponse = await spotify.Artists.GetAlbums(artist.Id, albumsRequest);
          requests++;

          var latestSingle = albumsResponse.Items
            .Select(album => new
            {
              Album = album,
              ReleaseDate = DateTime.TryParseExact(
                album.ReleaseDate,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTime date) ? date : (DateTime?)null
            })
            .Where(item => item.ReleaseDate.HasValue)
            .OrderByDescending(item => item.ReleaseDate)
            .FirstOrDefault();

          if (latestSingle != null)
          {
            if (((DateTime)latestSingle.ReleaseDate).Date == today)
            {
              Console.ForegroundColor = ConsoleColor.Blue;
              Console.WriteLine($"  Latest single: [{latestSingle.ReleaseDate.Value.ToShortDateString()}] {latestSingle.Album.Name}");

              var single = await spotify.Albums.Get(latestSingle.Album.Id);
              requests++;

              foreach (var track in single.Tracks.Items)
              {
                bool added = false;
                if (!track.Name.Contains("- Extended"))
                {
                  var fullTrack = await spotify.Tracks.Get(track.Id);
                  requests++;
                  if (!tempTracksToAdd.Any(item => item.ExternalIds.First().Value == fullTrack.ExternalIds.First().Value))
                  {
                    tempTracksToAdd.Add(fullTrack);
                    added = true;
                  }
                }
                if (added)
                {
                  Console.ForegroundColor = ConsoleColor.Green;
                  Console.Write($"    Added: [{track.TrackNumber}] ");
                }
                else
                {
                  Console.ForegroundColor = ConsoleColor.Yellow;
                  Console.Write($"    Skipped: [{track.TrackNumber}] ");
                }
                foreach (var trackArtist in track.Artists)
                  Console.Write($"{trackArtist.Name} ");
                Console.WriteLine($"- {track.Name}");
              }
              Console.ResetColor();
            }
            else
              Console.WriteLine($"  Latest single: [{latestSingle.ReleaseDate.Value.ToShortDateString()}] {latestSingle.Album.Name}");
          }
          else
            Console.WriteLine($"  No singles found for artist.");
        }
        catch (APIException ex)
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"Error: {ex.Message}");
          Console.ResetColor();
        }

        // Handle rate limit
        double msToSleep = ((double)requests / 3) * 1000;
        stopwatch.Stop();
        if (msToSleep > stopwatch.ElapsedMilliseconds)
        {
          var msLeftToSleep = msToSleep - stopwatch.ElapsedMilliseconds;
          Console.ForegroundColor = ConsoleColor.DarkGray;
          Console.WriteLine($"  Waiting {(int)msLeftToSleep}ms to not hit rate limit...");
          Thread.Sleep((int)msLeftToSleep);
          Console.ResetColor();
        }
        stopwatch.Start();
      }
      stopwatch.Stop();
      return tempTracksToAdd;
    }

    public async Task<List<PlaylistTrack<IPlayableItem>>> GetPlaylistTracks()
    {
      var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddUserSecrets<Program>()
        .Build();
      string playlistId = configurationBuilder.GetSection("Settings:PlaylistId").Value;

      var playlistTracks = new List<PlaylistTrack<IPlayableItem>>();

      try
      {
        int offset = 0;
        const int limit = 100;

        while (true)
        {
          var tracksPage = await spotify.Playlists.GetItems(playlistId, new PlaylistGetItemsRequest
          {
            Limit = limit,
            Offset = offset
          });

          playlistTracks.AddRange(tracksPage.Items);

          if (tracksPage.Next == null)
            break;

          offset += limit;

          Console.ForegroundColor = ConsoleColor.DarkGray;
          Console.WriteLine($"Waiting 350ms to not hit rate limit...");
          Thread.Sleep(350);
          Console.ResetColor();
        }
      }
      catch (APIException ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Error: {ex.Message}");
        Console.ResetColor();
      }

      return playlistTracks;
    }

    public async Task AddUniqueTracksToPlaylist(List<FullTrack> tracksToAdd, List<PlaylistTrack<IPlayableItem>> playlistTracks)
    {
      var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddUserSecrets<Program>()
        .Build();
      string playlistId = configurationBuilder.GetSection("Settings:PlaylistId").Value;

      //Check list for duplicates (needed since it is possible it was added before, if the track wasnt released at midnight)
      foreach (var trackToAdd in tracksToAdd)
      {
        if (!playlistTracks.Any(item => item.Track is FullTrack track && track.ExternalIds.First().Value == trackToAdd.ExternalIds.First().Value))
        {
          // Add track to playlist
          Thread.Sleep(1000); // Sleep 1s so order is kept in the playlist

          // Add track
          try
          {
            var request = new PlaylistAddItemsRequest(new List<string> { trackToAdd.Uri });
            await spotify.Playlists.AddItems(playlistId, request);
          }
          catch (APIException ex)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {ex.Message}");
            Console.ResetColor();
            return;
          }
          Console.ForegroundColor = ConsoleColor.Green;
          Console.Write("Added: ");
        }
        else
        {
          // Already in playlist
          Console.ForegroundColor = ConsoleColor.Yellow;
          Console.Write("Skipped: ");
        }
        foreach (var trackArtist in trackToAdd.Artists)
          Console.Write($"{trackArtist.Name} ");
        Console.WriteLine($"- {trackToAdd.Name} ");

        Console.ResetColor();
      }
    }

    public async Task RemoveOldTracksFromPlaylist(List<PlaylistTrack<IPlayableItem>> playlistTracks)
    {
      var configurationBuilder = new ConfigurationBuilder()
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddUserSecrets<Program>()
        .Build();
      string playlistId = configurationBuilder.GetSection("Settings:PlaylistId").Value;
      DateTime dateAddedThreshold = DateTime.UtcNow.AddDays(-1 * Int32.Parse(configurationBuilder.GetSection("Settings:DateAddedThreshold").Value));

      var tracksToRemove = new List<PlaylistRemoveItemsRequest.Item>();

      foreach (var item in playlistTracks)
      {
        if (item is PlaylistTrack<IPlayableItem> playlistTrack && playlistTrack.Track is FullTrack track)
        {
          if (playlistTrack.AddedAt < dateAddedThreshold)
          {
            tracksToRemove.Add(new PlaylistRemoveItemsRequest.Item { Uri = track.Uri });
          }
        }
      }

      if (tracksToRemove.Count > 0)
      {
        const int tracksMax = 100;
        var trackBatches = tracksToRemove
          .Select((track, index) => new { track, index })
          .GroupBy(x => x.index / tracksMax)
          .Select(group => group.Select(x => x.track).ToList())
          .ToList();

        try
        {
          foreach (var batch in trackBatches)
          {
            var request = new PlaylistRemoveItemsRequest { Tracks = batch };
            await spotify.Playlists.RemoveItems(playlistId, request);
            Console.WriteLine($"Removed {batch.Count} tracks from the playlist.");

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"Waiting 350ms to not hit rate limit...");
            Thread.Sleep(350);
            Console.ResetColor();
          }
        }
        catch (APIException ex)
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine($"Error: {ex.Message}");
          Console.ResetColor();
        }
      }
      else
      {
        Console.WriteLine("No tracks to remove.");
      }

    }
  }
}
