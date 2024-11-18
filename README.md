# DailyReleaseRadar

Catch all the latest music from artists you follow. Updates daily.

## App settings

This program can be configured via the appsettings.json file. It is recommended to overwrite certain keys via user secrets if you're committing your code to git. 

|Setting                       |Type  |Details|
|------------------------------|------|-------|
|Spotify:ClientId              |string|Client ID, can be found under `settings` → `Basic Information` in your Spotify app dashboard|
|Spotify:ClientSecret          |string|Client secret, can be found under `settings` → `Basic Information` in your Spotify app dashboard|
|Spotify:RedirectUri           |string|Redirect URI, must be same as the one in your Spotify app|
|Spotify:RefreshToken          |string|Refresh token (optional), can be used so no login is required every time the program is executed. After first login, the console will print your refresh token that can saved here|
|Settings:PlaylistId           |string|Spotify (Daily Release Radar) playlist ID, example: `6OBMcaBurzTq78EPDiVXWl`|
|Settings:DateAddedThreshold   |int   |Days after which a track is removed from the playlist, default 7||
