# Changelog

All notable changes to `TakeOverBot` will be documented in this file.

Updates should follow the [Keep a CHANGELOG](https://keepachangelog.com/) principles.

## Unreleased

### Added
- `/voterappel` command
  - **Admin and Staff Only**
  - Relance manuellement les membres du rôle cible qui n'ont pas voté, dans le salon du sondage.
  - Option `message_id` pour cibler un sondage précis ; sinon le dernier sondage actif du salon.

### Fixed
- Les rappels automatiques de vote (mi-durée et expiration) envoient désormais les mentions par lots, avec `AllowedMentions` explicites, pour que les @ notifient réellement même avec beaucoup de membres.
- Les erreurs de rappel de vote sont loguées dans le salon logs (et Sentry si activé) au lieu d'être uniquement écrites en console.

## 2026.4.1-hotfix2 - 10/04/2026

### Fixed
- Fixed a bad condition checking which results in loop posting

## 2026.4.1-hotfix1 - 10/04/2026

### Fixed
- Fixed a miss for pending post which results in loop posting

## 2026.4.1 - 10/04/2026
Rework of the bot. 100% rewritten in C#.

### Added
- Clear User command
  - **Admin Only** 
  - This command will delete all the messages of a specific user in a specific timeframe.
- Clear Message command 
  - **Admin Only**
  - This command will delete all the messages of a specific channel until a specific message.
- Contact command
  - This command will allow users to contact the staff or admins by creating a specific channel.
- End contact command
  - This command will close a contact channel.
- Create a poll command
  - **Admin and Staff Only** 
  - This command will allow users to create a poll.
  - It will remind every targetted user to vote when the poll is halfway.
  - It will close the poll when the time is up and ping every user that didn't vote for at least one option.
- Help command
  - This command will list all the commands of the bot.
  - You can have the help for a specific command by using the command `/help <command>`.
- Mute command
  - **Admin Only**
  - This command will mute a specific user for a specific timeframe.

### Fixed
- Better crash handling.
- Completely reworked the instagram post-listener to use official Facebook Graph API.
  - Scrapping a post is the only way i've found to handle collaborations.

### Changed
- Migrated from MongoDB to SQLite.
- Emergencies
  - Emergencies are now handled with the last message instead of the first one. Which means that the emergency will now only be considered ended after a specific amount of inactivity.

## v0.1.1 - 09/10/2025

### Added
- Docker image
- [WIP] Role website updater

### Fixed
- YouTube and Instagram watcher crashing the entire bot

### Changed
- Convert config.json to .env

### Removed
- Steam client

## v0.1.0 - 24/05/2025

### Added
- Auto post new Instagram Post or Reel on a specific discord channel
- Auto post new YouTube videos or shorts on a specific discord channel
- DOM Parser package
- GuildMessageReactions Discord client's intent
- Emergency Listener V1 (User send messages on a specific channel, if the channel was inactive for X minutes, bot ask all the users if it's a real emergency. Yes => Bot pings everyone, No => Bot delete messages)
- Command "/send" to allow a user with a specific role to send messages as the bot.
- [WIP] Add role based on reactions on bot's messages.
- CreateIndex MongoDB client

### Changed
- UserJoinLeave is now UserJoinLeaveListener

### Removed
- Some useless console.log()

## v0.0.2 - 11/02/2025

### Added
- Auto add "Visiteur" role when rules are accepted

### Fixed
- Date time not displaying correctly when on embeds

## v0.0.1 - 07/02/2025

### Added
- `/ping` Pong !
- Listener on member entry
- Listener on rules accepted by the new member
- Welcome message when new member accepted the rules
- Discord.JS 14
- MongoDB 6 (Don't know if i will keep it)
