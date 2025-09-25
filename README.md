# DoorTelnet

A modern .NET 8 telnet client specifically designed for MUD (Multi-User Dungeon) gaming, with advanced automation and game analysis features.

## The Story Behind DoorTelnet

I was discussing old BBS (Bulletin Board System) games with colleagues at work when nostalgia hit hard. Those conversations about the golden age of dial-up gaming got me curious about what happened to my favorite childhood game: **Rose Council of Guardians**. After lots of research, I discovered that the wonderful folks at [thepenaltybox.org](http://thepenaltybox.org) still maintain a licensed server running the game!

But here's the thing - I wanted more than just a basic telnet connection. I wanted to customize my experience, automate the tedious parts, and have some serious fun with the game mechanics. So I built DoorTelnet from scratch as a modern, feature-rich client that brings classic MUD gaming into the 2020s.

## ?? What Makes DoorTelnet Special

### Advanced Terminal Engine
- **Full ANSI Support**: Perfect rendering of classic BBS art and color schemes
- **Smart Text Processing**: Intelligent parsing of game output with combat and room detection
- **Configurable Display**: Customize terminal size, fonts, and visual preferences

### Intelligent Combat System
- **Real-time Combat Tracking**: Monitor damage dealt/taken, combat duration, and experience gains
- **Monster Targeting**: Advanced monster identification and targeting system
- **Combat Statistics**: Detailed analytics of your fighting performance
- **Death Detection**: Automatic recognition of monster deaths and combat endings

### Room Mapping & Navigation  
- **Automatic Room Detection**: Parses room descriptions, exits, and monster presence
- **Dynamic Room Tracking**: Real-time updates of room contents and monster states
- **Smart Text Filtering**: Separates room content from combat spam and system messages

### Character Management
- **Multiple Character Profiles**: Manage different characters with individual settings
- **Persistent Character Data**: Tracks experience, levels, spells, inventory, and more
- **Secure Credential Storage**: Encrypted storage of login credentials
- **Character Sheet Integration**: View detailed character stats and progression

### Powerful Automation Features
- **Auto Gong**: Automated monster summoning, combat, and treasure collection
- **Auto Attack**: Intelligent targeting and attacking of aggressive monsters  
- **Auto Shield**: Maintains magical protections automatically
- **Auto Heal**: Smart healing based on configurable HP thresholds
- **Loot Collection**: Automatic pickup of gold, silver, and specified items
- **Critical Health Protection**: Emergency actions when health gets dangerously low

### Scripting & Customization
- **Lua Script Support**: Write custom automation scripts
- **Login Scripts**: Automate connection and character selection
- **Flexible Configuration**: Extensive settings for every aspect of gameplay
- **Hot Keys**: Customizable keyboard shortcuts for common actions

## ?? Key Features

### ??? Safety First
- **Encrypted Credentials**: Your login information is securely encrypted
- **Safe Automation**: Built-in safeguards prevent dangerous situations
- **Configurable Thresholds**: Set your own limits for automated actions
- **Emergency Shutoffs**: Automatic disconnection on critical health

### ?? Game Intelligence  
- **Spell Database**: Tracks your available spells, mana costs, and spheres
- **Inventory Management**: Real-time inventory tracking and organization
- **Experience Tracking**: Monitor XP gains, levels, and progression
- **Combat Analytics**: Detailed statistics on your fighting effectiveness

### ?? Smart Automation
- **Context-Aware**: Automation responds intelligently to game situations
- **Monster Recognition**: Advanced parsing of monster names and states
- **Room Awareness**: Actions based on current room contents and exits
- **Timer Management**: Respects game cooldowns and timing restrictions

### ??? Modern Interface
- **WPF Application**: Native Windows interface with modern styling
- **Real-time Updates**: Live display of character stats, room info, and combat data
- **Configurable Layout**: Customize the interface to your preferences
- **Debug & Logging**: Comprehensive logging for troubleshooting and optimization

## ?? System Requirements

- **OS**: Windows 10/11 (x64)
- **Framework**: .NET 8.0 Runtime
- **Memory**: 50MB+ available RAM
- **Network**: Internet connection for MUD server access

## ?? Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract the ZIP file to your preferred location
3. Run `DoorTelnet.Wpf.exe`
4. Configure your connection settings for thepenaltybox.org (or your preferred server)

## ?? Quick Setup

1. **Configure Connection**: Settings ? Connection ? Set host to `thepenaltybox.org` port `23`
2. **Add Credentials**: Credentials ? Add your username and password
3. **Connect & Login**: Use Quick Login for automated connection
4. **Set Automation**: Character Profiles ? Configure your automation preferences
5. **Start Playing**: Enable Auto Gong or Auto Attack and enjoy!

## ?? Planned Features

I'm actively developing DoorTelnet with exciting features planned:

### ??? Advanced Mapping
- **Interactive Maps**: Visual room layouts with navigation
- **Map Database**: Persistent storage of explored areas  
- **Pathfinding**: Automated navigation between known locations
- **Area Analysis**: Statistical analysis of different game zones

### ?? Monster Intelligence
- **Monster Database**: Comprehensive monster stats and behaviors
- **Spawn Tracking**: Monitor monster appearance patterns
- **Difficulty Analysis**: Assess monster threat levels
- **Optimal Targeting**: AI-driven target selection

### ?? Item Management  
- **Item Database**: Complete catalog of game items and properties
- **Inventory Optimization**: Smart item management and storage
- **Equipment Analysis**: Compare weapons, armor, and accessories
- **Trade Value Tracking**: Monitor item values and market trends

### ?? Advanced Analytics
- **Performance Metrics**: Detailed analysis of gameplay efficiency  
- **Progression Tracking**: Long-term character development insights
- **Economic Analysis**: Track wealth accumulation and spending patterns
- **Achievement System**: Custom goals and milestone tracking

## ?? Contributing

Found a bug? Want to suggest a feature? Contributions are welcome!

- **Issues**: Report bugs or request features via GitHub Issues
- **Pull Requests**: Code contributions are appreciated
- **Feedback**: Share your experiences and suggestions

## ?? License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## ?? Acknowledgments

- **The Penalty Box Team**: For keeping Rose Council of Guardians alive and well
- **The MUD Community**: For preserving these amazing games
- **Fellow BBS Enthusiasts**: For sharing the nostalgia that started this project

## ?? Disclaimer

DoorTelnet is designed for educational and entertainment purposes. Please respect the rules and policies of any MUD server you connect to. The authors are not responsible for any actions taken using this software.

---

*Remember: The best part of MUDding isn't the destination - it's the adventure along the way. DoorTelnet just makes that adventure a little more convenient!* ??