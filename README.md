# Discord Bot Take Over Motorsport

Take Over Motorsport is a car enthusiast brand that aims to connect enthusiasts and car industry professionals.

- Website / Shop : [takeovermotorsport.com](https://takeovermotorsport.com)
- Instagram : [@takeovermotorsport](https://www.instagram.com/takeovermotorsport)
- Facebook : [Take Over Motorsport](https://www.facebook.com/profile.php?id=61558450501564)
- Discord : [discord.gg/xv4AtYdw6X](https://discord.gg/xv4AtYdw6X)

This bot is designed to help the discord's staff to manage server and to enhance the user experiment.

## Command list

This bot will probably don't have a lot of command as it needs to be basic to use

- **Admin** : `/clearmsg {messageId}` Clear messages from a channel until the specified message.
- **Admin** : `/clearuser {user} {duration} {days or hours}` Clear messages from a user in every channel until a specified time.
- **Admin** : `/mute {user} {duration} {days or hours}` Mute a user for a specific duration.
- **Admin or Staff** : `/vote {staff or admin} {question} {choices (separated by commas)} {duration} {enable multi select}` Create a poll with reminder.
- **Admin or Staff** : `/rappelvote {message id (optional)}` Ping and DM members who can see the channel and have not voted. Without `message_id` it targets the latest active poll in the current channel. Automatic reminders also run 72h, 24h and 3h before a poll ends.
- **Admin or Perm Bot** : `/send {channel} {message}` Send a message as the bot only if the user as a specific role.
- `/contact {staff or admin}` Create a specific channel for the user to contact the staff or the admin.
- `/endContact` Close the contact channel.
- `/help {command}` Display the help of a specific command or a list of all the commands.
- `/ping` Pong !

## Listeners

- User join
- User has accepted the rules
- User leave
- Emergency
- [WIP] Reaction Role

## Watchers

- New Instagram post or reels from takeovermotorsport account..

## Requirements

- .NET 10

## Install

### Native

- Copy `.env.local.template` to `.env.local` and fill it
- Install the dependencies

```bash
$ dotnet restore
```

- Run the bot

```bash
$ dotnet build && dotnet run
```

## ChangeLog

Please see [CHANGELOG](CHANGELOG.md) for more information on what has changed recently.

## Contributing

Please see [CONTRIBUTING](CONTRIBUTING.md) for details.

## Security

If you discover any security related issues, please create a new issue with the corresponding filters on [GitHub - Issues](https://github.com/KriKrixs/Discord-BOT-CSTracker/issues/new).
