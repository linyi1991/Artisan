<!-- Repository Header Begin -->
<div align="center">
<img src="https://love.puni.sh/resources/artisan.svg" alt="Artisan IconUrl" width="15%">
<br>
<img src="https://github.com/PunishXIV/Artisan/blob/050a58be7b0ce94c959c17e43dabecb65e38a55c/PunishImages/artisan.png" width="30%" />

Crafting Automation Plugin/Helper for FFXIV

[![image](https://discordapp.com/api/guilds/1001823907193552978/embed.png?style=banner2)](https://discord.gg/Zzrcc8kmvy)

Repo Url: 

`https://love.puni.sh/ment.json`
</div>

## API13 fork synchronization

This fork pins `OtterGui` at `dd3573461356dbd45cc1750a2cd16d5c1f51da21`
and `PunishLib` at `e200256e1d997b2513358af883a29b6f1101fdb2`.
The original upstreams no longer advertise those commits, so both nested
submodules use `linyi1991` forks with branch `api13-artisan-pin`. This keeps
recursive clones reproducible without changing Artisan runtime code.
