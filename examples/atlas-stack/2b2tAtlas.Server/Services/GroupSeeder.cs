using Microsoft.EntityFrameworkCore;
using _2b2tAtlas.Server.Models;

namespace _2b2tAtlas.Server.Services;

/// <summary>
/// Seeds/refreshes the canonical set of well-known 2b2t groups (build + highway)
/// used for attribution (GAMEPLAN Â§15). Wiki URLs point at the official 2b2t
/// WikiOasis-hosted 2b2t Wiki.
///
/// This runs every startup and is idempotent: it adds missing canonical groups and
/// performs one enrichment backfill when a legacy row has none of the new sourced
/// metadata. Subsequent administrator edits remain authoritative across restarts.
/// Groups created ad-hoc in the admin UI are never in this list and are left untouched.
/// </summary>
public class GroupSeeder
{
    private readonly AtlasContext _context;
    private readonly ILogger<GroupSeeder> _logger;

    /// <summary>Initializes canonical group metadata synchronization.</summary>
    /// <param name="context">The Atlas context containing group attribution records.</param>
    /// <param name="logger">The logger for added and refreshed canonical rows.</param>
    public GroupSeeder(AtlasContext context, ILogger<GroupSeeder> logger)
    {
        _context = context;
        _logger = logger;
    }

    private record Canon(
        string Name,
        string Type,
        string Desc,
        string? Wiki,
        string? Color,
        string? LogoUrl,
        string? Founded,
        string? Status,
        string? WebsiteUrl = null,
        string? DiscordUrl = null,
        string[]? Aliases = null,
        string[]? Locations = null,
        string LocationRole = "Builder");

    private const string Wiki = "https://2b2t.wikioasis.org/wiki/";

    private static readonly Canon[] Canonical =
    {
        // ---- Build groups ----
        new("Nerds Inc", "Other", "Technical and griefing group also known historically as The Tyranny. Its members developed the Nocom coordinate exploit and published its implementation and explanation. Its public projects also document an earlier Minecraft authentication exploit.",
            null, "#607d8b", "https://github.com/nerdsinspace.png", "2016", null,
            WebsiteUrl: "https://github.com/nerdsinspace", Aliases: ["Nerds Inc.", "The Tyranny"]),
        new("SpawnMasons", "Build", "Secretive building fraternity founded by HermeticLock in early 2017. Its weekly meetings produced hundreds of temporary lodges as well as long-term bases, monuments, experiments, and collaborative builds near spawn and farther out.", Wiki + "SpawnMasons", "#c8a45a", "https://static.wikitide.net/2b2twiki/thumb/c/ce/SpawnMason_logo.png/512px-SpawnMason_logo.png", "Early 2017", "Active",
            Locations: ["Space Jam", "Vulcatavio", "COVID-2147", "Smibville", "Photosynthesis", "Cloud Club", "Rat House", "Autistralia", "Chunk Haven", "Sky Masons",
                "Abstain Lodge", "Angler Catfish Flushed", "Big Cave", "Pyrobyte Resummoning Experiment",
                "Secret Santa Mason Stats Christmas", "Shit and Cum and 69", "Underground Bath"]),
        new("Valkyria", "Other", "Influential 2013-2015 faction formed from several earlier groups and bases. Valkyria combined builders, redstoners, and PvP players, led the first three Spawn Incursions, and left some of early 2b2t's most recognizable bases and symbols.", Wiki + "Valkyria", "#b06bd6", "https://static.wikitide.net/2b2twiki/3/36/Valkyria_Mashup.png", "April 29, 2013", "Disbanded",
            Aliases: ["Space Valkyria"], Locations: ["Valkyria", "Rhadamantis", "Asgard I", "Asgard 2", "Valhalla", "Fenrir", "Space Valkyria", "Space Valkyria II", "Space Valkyria III", "Space Valkyria III (original)", "Space Valkyria III (first site)", "Space Valkyria III (second site)", "Space Valkyria III rebuild (2025)", "Space Valkyria IV staging area", "Space Valkyria Island", "The Bastion", "Wrath Outpost"]),
        new("Vortex Coalition", "Other", "Large faction founded by Coltsnid and AmericanOreo in 2015. VoCo operated through several phases, maintained a large community and its own economy, absorbed the Spawn Infrastructure Group, and disbanded in March 2021.", Wiki + "Vortex_Coalition", "#4da3ff", "https://static.wikitide.net/2b2twiki/thumb/d/d0/VoCoPhaseIII.jpg/512px-VoCoPhaseIII.jpg", "May 2015", "Disbanded", WebsiteUrl: "https://www.thevortexcoalition.com/",
            Locations: ["Aaenidaad", "Trinity Base", "R'Lyeh", "Epsilon", "Hidden City of Pantheon", "Wrath of VoCo"]),
        new("The Emperium", "Build", "Long-running 2b2t faction founded by TheDark_Emperor and rootbeerguy1212 in December 2016. Although it also organized conflicts and events, the Atlas classifies it by its unusually long succession of member bases across multiple eras.", Wiki + "The_Emperium", "#d4af37", "https://static.wikitide.net/2b2twiki/c/cc/EmpLogo.jpg", "December 1, 2016", "Active", Aliases: ["Emperium"],
            Locations: ["Empire's Edge", "WÃ¤ssrige HÃ¶lle Zwei", "Castle Blackstone", "Gore Teck's Swamp", "Capital Hardknott re inhabited", "UncoTown", "UncoTown 2", "Tartarus", "Nova Satus", "Oblivion", "Angkor", "Empire's Larry", "Empire's Africa", "Emperium Jungle base", "Trinity Base", "Spic Town", "Ventir Zeta", "Triumph Retreat", "Lad Leuthil", "Sleepy's Hollow", "Whitewatch II", "Sigma Pride", "Sigma Camp", "Sierra"]),
        new("The Imperials", "Build", "Multi-server faction whose 2b2t branch says it was established by Titus on June 1, 2016. The active branch publishes community, auxiliary, spawn, and numbered builder bases; documented projects include Caledonia, Oceania I, Ambrose I, Windhelm I, Lente II, and Aqueous VI. It is distinct from The Emperium and SpawnMasons.", "https://the2b2twiki.wordpress.com/2021/04/17/the-imperials/", "#b71c1c", "https://cdn.discordapp.com/icons/731911760948363275/a_a10e7cf0579a84e0fcdeecabc527094f.png?size=512", "2b2t branch: June 1, 2016", "Active", WebsiteUrl: "https://imps-join.com/2b2t", DiscordUrl: "https://discord.gg/spawnmasons",
            Locations: [
                "Aphelion", "Caledonia", "Ex Animo", "Imperatoria 23", "Oceania", "Oceania I", "Ambrose I", "Windhelm I", "Lente II", "Aqueous VI", "Imperial Holiday base", "Red Fort", "Tyranny", "Varenum",
                "Recruit Fortress", "New Lorien", "Port Majula", "Outpost Arcadia", "Recruit Outpost I", "Recruit Outpost III", "Trust Outpost",
                "Travellers Outpost", "Outpost MR", "Fort Redtail", "Imperial City", "Notaufred town", "Karthwasten Recruit Base", "Karthwasten",
                "Imperia Recruit Base", "Imperialisium 66", "Arminium", "Kyne's Keep", "Florentia", "Ullr", "Ashhaven", "Eboracum", "Arverni",
                "Varenum", "Edgeville", "Belisaria", "Raven Rock", "Oceanside I", "Arabona", "Voltra", "Vexmoor II", "Skybound I", "Eregion I",
                "Lakeside I", "Aquilo I", "Fih I", "Bierres I", "World Border Freakery", "ImpLands", "ImpLands II", "Yeer I", "Loposhome I",
                "Loposhome", "Emfinity I", "Evere I", "Milia I", "Vexmoor I"]),
        new("Team Veteran", "Other", "Loose anti-Rusher coalition formed in June 2016 during the Rusher War. It organized established players against TheCampingRusher's influx, primarily through spawn PvP and the Fourth Incursion rather than as a conventional building group.", Wiki + "Team_Veteran", "#6aa84f", "https://static.wikitide.net/2b2twiki/2/24/KQp3Rbp.png", "June 16, 2016", "Disbanded"),
        new("Backstreet Boys", "Other", "Group founded by digandbuilder and iMems in September 2018. BSB has pursued an intentionally broad mix of social activity, PvP, base building, and high-profile griefs, and is especially known for its recorded griefing history.", Wiki + "Backstreet_Boys", "#3c78d8", "https://static.wikitide.net/2b2twiki/f/fa/Backstreet_Boys.png", "September 2018", "Active"),
        new("DonFuer", "Build", "Long-running building community founded outside 2b2t in 2013 and active on 2b2t since 2016. DonFuer promotes organized collaborative building through a numbered series of flagship bases and many smaller projects.", Wiki + "DonFuer", "#8e7cc3", "https://static.wikitide.net/2b2twiki/thumb/5/5a/Donfuer_Coat_of_Arms.png/512px-Donfuer_Coat_of_Arms.png", "August 2013; on 2b2t since November 2016", "Active", WebsiteUrl: "https://donfuer.com/", DiscordUrl: "https://discord.gg/nDnttv9xwk",
            Locations: ["10th Don Fuer", "11th Don Fuer", "12th Don Fuer", "DonFuer 20", "DonFuer 21", "Don Fuer 22", "DonFuer 23", "DonFuer 25", "DonFuer 27", "DonFuer 28", "DonFuer", "Don Blaahaj", "Don Myr", "SteamFuer", "Aureus City", "Blue Castle", "Don Maximus", "FutureFuer", "The Dark Mirror", "MooMoo Cowtown", "Don Chykukanukakisaki", "Cherry Town", "Arendal", "AssFuer"]),
        new("Imperator's Group", "Build", "Small early building group active around 2012-2015. Its principal project was Imperator's Base, whose monumental Christ the Redeemer statue became one of the best-known builds in 2b2t history.", Wiki + "Imperator%27s_Group", "#cc0000", "https://static.wikitide.net/2b2twiki/thumb/2/26/Impsbase.png/512px-Impsbase.png", "2012", "Disbanded", Locations: ["Imperator's Base (Jesus Statue)"]),
        new("Spawn Builders Association", "Build", "Open building group founded in April 2021 and dedicated to creating and maintaining spawn bases. Rather than concentrating on one permanent headquarters, SBA directs most of its effort into successive spawn projects.", Wiki + "Spawn_Builders_Association", "#93c47d", "https://static.wikitide.net/2b2twiki/thumb/d/d2/2022-04-02_17.52.07.png/512px-2022-04-02_17.52.07.png", "April 5, 2021", "Active", Locations: ["Larptown", "The Valley of Jesus"]),
        new("Monument Construction Group", "Build", "Small building group founded in 2021 to construct monuments at major server milestones and historically significant coordinates.", Wiki + "Monument_Construction_Group", "#b45f06", "https://static.wikitide.net/2b2twiki/e/ea/MCGTEMPLOGO.png", "2021", "Active"),
        new("Core Builders", "Mixed", "Building and infrastructure community founded in June 2026. Active on 2b2t and other Minecraft servers, it focuses on collaborative projects, helping players, repairing infrastructure, and sharing resources.", Wiki + "User:AwesomeHeroz/Drafts/CoreBuilders", "#c96cff", "https://corebuilders.gg/assets/logo.webp", "June 2026", "Active", "https://corebuilders.gg/", "https://discord.gg/corebuilders"),
        new("Crimson Star", "Other", "Short-lived building and social group founded in August 2020 by B0dait, Fierceben, Hurtmercury, and y_a_t_a. Its members worked on a succession of collaborative bases, including the Base Nostalgia rebuild and the first two Cum Zone projects, before the community fragmented and ultimately merged into the Shortbus Caliphate.", Wiki + "Crimson_Star", "#b3263e", "https://static.wikitide.net/2b2twiki/2/26/Crimson_Star_Discord_Icon.png", "August 21, 2020", "Merged into Shortbus Caliphate"),
        new("DGA", "Other", "Alliance of roughly a dozen small groups founded by SoiledCold in July 2019. Also known as the Democratic Group Alliance and Small Groups Alliance, it coordinated shared projects such as DGA Capital and Base Unity before becoming inactive.", Wiki + "Democratic_Group_Alliance", "#486b9b", "https://static.wikitide.net/2b2twiki/6/64/SGAv2_Banner.png", "July 20, 2019", "Inactive", Aliases: ["Democratic Group Alliance", "Small Groups Alliance"]),
        new("Followers of the Crafting Table", "Other", "Crafting-table-themed religious and building community founded by villicool112 and Yarnamite in September 2019. Its best-known project was the Valley of Crafting Tables near 25,000, 300, where members placed roughly 300,000 crafting tables.", Wiki + "Followers_of_the_Crafting_Table", "#9a6b35", "https://static.wikitide.net/2b2twiki/d/d9/Mcpe-outcome-ctable.png", "September 2019", "Semi-Active"),
        new("The Republic", "Other", "Spanish-speaking building community founded by Bartolome_Mitre during the December 2017 Spanish influx associated with ElRichMC. It established La Capital and a network of later settlements, outposts, embassies, and roads while maintaining its own civic role-play identity.", Wiki + "The_Republic", "#506ea8", "https://static.wikitide.net/2b2twiki/3/3d/Greece-38803_960_720.png", "December 2017", "Active", Locations: ["La Capital", "Nuremberg"]),
        new("Astral Brotherhood", "Build", "Private building group founded by Joey_Coconut and LordGalvatronMC in August 2021. Its long succession of main, recruit, and meteorite bases includes Menegroth, Astralia, Nautilus, Solaris, Stellaris, and many later projects.", Wiki + "Astral_Brotherhood", "#7067cf", "https://static.wikitide.net/2b2twiki/5/5c/Astral_Brotherhood_Logo.png", "August 22, 2021", "Active",
            Locations: ["Aeolus", "Astraeus", "Astralia", "Capybara Castle", "Dubrovnik", "Durkaa's Farm", "Elsweyr", "End Gape", "Fatlantis", "Helheim", "Helheim 2", "Hintertown 6", "Lidenbrock", "Lumire", "Menegroth", "Molaris", "Nautilus", "Neritum", "New Lugdunum", "New Menegroth", "Obsidian ISle", "Okab", "Poppy Town", "Riga", "Skylight's Haven", "Solaris", "Solera", "Stellaris", "Taiyo", "The Rift", "Vega", "Vocitare", "Vouchbase"]),
        new("Aven Alliance", "Build", "Private building community founded by KalebLP and Tilley8 in August 2024 with an emphasis on a small, close-knit membership.", Wiki + "Aven_Alliance", "#5c88da", "https://static.wikitide.net/2b2twiki/5/55/AvenAlliance.png", "August 1, 2024", "Active", Locations: ["Vivelia"]),
        new("Bedrock City", "Build", "Building group and numbered base series founded by usuriousberry39 in November 2022. Its projects use Bedrock Edition injections as a defining construction rule and deliberately maintain neutrality toward other groups.", Wiki + "Bedrock_City", "#8c8c8c", "https://static.wikitide.net/2b2twiki/b/b7/BedRockCity.png", "November 2022", "Active", Locations: ["Bedrock City", "Bedrock City 2"]),
        new("Elysium", "Build", "Small social and building group founded and led by Carlll_ in March 2019. It was intentionally kept close-knit and also maintained a substantial presence on 9b9t before disbanding.", Wiki + "Elysium", "#8d72b8", "https://static.wikitide.net/2b2twiki/8/8d/ElysiumIcon.gif", "March 19, 2019", "Disbanded", Locations: ["Elysium", "Elysium 2"]),
        new("Fellowship of the Diamond", "Other", "One of 2b2t's earliest known groups, first active in 2011 and briefly revived in 2015. Its members generally kept to themselves while maintaining several early bases around spawn.", Wiki + "Fellowship_of_the_Diamond", "#55a9c7", "https://static.wikitide.net/2b2twiki/7/7f/Diamond_Town_Wheat_Farm.jpg", "August 2011; revived March 2015", "Disbanded", Locations: ["700Base"]),
        new("Guardsmen", "Build", "Building group founded in May 2020 by HermeticLock, TheDark_Emperor, D_loaded, and Orsond. Its central project was the repeatedly rebuilt Homeland near spawn, alongside a series of other bases and projects.", Wiki + "Guardsmen", "#8695a8", "https://static.wikitide.net/2b2twiki/5/58/Guardsman_Logo_Transparent.svg", "May 2020", "Disbanded", Locations: ["Adamantium", "Camp Spooky", "Canopy", "Mediano", "The Crypt", "The Homeland", "Whistler"]),
        new("Invictus", "Build", "Base group created during the 2016 Rusher influx. After its two principal bases, many of its members went on to help form Block Game Mecca.", Wiki + "Invictus", "#ca9b42", "https://static.wikitide.net/2b2twiki/5/58/Invictus_Banner.png", "July 2016", "Disbanded", Locations: ["Invictus I", "Invictus II"]),
        new("Judge's Group", "Build", "Early group founded by Offtopia and THEJudgeHolden in 2011. It escorted new players from spawn and created Old Town before expanding into a sequence of important early bases including Ravendel and The Drain.", Wiki + "Judge%27s_Group", "#9d7358", "https://static.wikitide.net/2b2twiki/c/cb/Rapture1.png", "2011", "Disbanded", Locations: ["Old Town", "Rapture I", "Rapture II", "Ravendel", "The Drain"]),
        new("Midnight Council", "Build", "Exclusive building group founded by HermeticLock in December 2022. Its members assemble for a short annual session to construct a shared base.", Wiki + "Midnight_Council", "#28345e", "https://static.wikitide.net/2b2twiki/6/63/Midnight_Council_Logo.png", "December 5, 2022", "Active", Locations: ["Midnight Council 2022"]),
        new("Obscension", "Build", "Group founded in March 2021 that built several bases and became best known for organizing the recurring public PitFight events.", Wiki + "Obscension", "#8065ad", "https://static.wikitide.net/2b2twiki/2/27/Obscension_Logo.png", "March 2021", "Active", Locations: ["Boletus", "Heimsland", "Obscension", "Pit Fight", "Pit Fight: Arena", "Pit Fight 4", "Pit Fight 6", "Pit Fight 7", "Pit Fight 8: Hijacked", "Pit Fight 9", "Pit Fight 10", "Pit Fight 11", "Pit Fight 13", "Pit Fight 14", "Pit Fight 15"]),
        new("Shortbus Caliphate", "Build", "Long-running social and building faction founded in November 2017, also known as SBC, FunGroup, and Gaza. Its members built or shared a succession of bases while maintaining an intentionally irreverent public identity.", Wiki + "Shortbus_Caliphate", "#d2aa59", "https://static.wikitide.net/2b2twiki/9/9e/ShortbusCaliphateLogo.jpg", "November 6, 2017", "Inactive", Aliases: ["SBC"], Locations: ["Adamantium", "Beirut", "Cum Zone 2", "Fort Aqua", "Mediano", "Shortbus Caliphate", "Whistler"]),
        new("Sun Knights", "Build", "Building group founded by CuppyK in February 2022. It has operated through several leadership eras and a succession of collaborative bases.", Wiki + "Sun_Knights", "#e8b638", "https://static.wikitide.net/2b2twiki/e/eb/204b22ea50ce564b6fe32462234c55d1.png", "February 18, 2022", "Active", Locations: ["Sun Knight's Workshop", "Sunzy Castle", "Winter Beard"]),
        new("Team Rainbow", "Build", "Building group founded by Brayin in August 2017 after Team Aurora became inactive. It continued through several projects before its own activity declined.", Wiki + "Team_Rainbow", "#65b66a", "https://static.wikitide.net/2b2twiki/1/1c/New_Rainbow_Islands_%28Rainbow_Republic%29_2018-04-28_Chunky_render.jpeg", "August 11, 2017", "Inactive", Locations: ["Rainbow Forest", "The New Rainbow Islands"]),
        new("The Last Templar", "Build", "Exclusive building group founded in March 2018 by King_Aurelius, GenderPretender, and ForRussia. It was known for Argonath, New Argonath, and its 10k highway monuments.", Wiki + "The_Last_Templar", "#b7a45f", "https://static.wikitide.net/2b2twiki/a/ab/Templar_cross.png", "March 4, 2018", "Disbanded", Locations: ["Argonath", "Costco Base", "New Argonath"]),
        new("The Mew Revolution", "Build", "Community and building group founded by MrAllNet in December 2020 to connect players through shared bases, projects, and voice chats.", Wiki + "Mew_Revolution", "#d68fc3", "https://static.wikitide.net/2b2twiki/8/8d/Mew_logo_with_black_bg.png", "December 2020", "Disbanded", Aliases: ["Mew Revolution"], Locations: ["Bombay", "Easter Island"]),
        new("Vapepens Elite Alliance", "Build", "Spawn-oriented building group founded in September 2022, commonly abbreviated VEA. It has created numerous community bases in and around spawn while maintaining a deliberately informal culture.", Wiki + "Vapepens_Elite_Alliance", "#dd6292", "https://static.wikitide.net/2b2twiki/8/85/VEA_logo.png", "September 2022", "Active", Aliases: ["VEA"], Locations: ["Animal Kingdom", "Asstopia", "Chromatown", "Cream Town", "Deadwood", "Farmville", "Lockwood", "Monkey Hole", "Scoobert Land", "Slootland", "Stalpo Street", "USA", "Ven's Village"]),
        new("Wingston", "Build", "Private spawn building group founded and led by Warske in November 2022. Each numbered base centered on a recognizable viewing tower before the group became inactive.", Wiki + "Wingston", "#7187a3", "https://static.wikitide.net/2b2twiki/c/c7/Wingston_Group_picture.png", "November 1, 2022", "Inactive", Locations: ["Wingston III", "Wingston V"]),
        new("EveryoneBase", "Build", "Community building project founded by Joey_Coconut to bring members of otherwise separate 2b2t groups together for a shared base.", Wiki + "EveryoneBase", "#69a66f", "https://static.wikitide.net/2b2twiki/b/b7/EveryoneBase_Render_1.png", null, "Unknown", Locations: ["Everyone Base"]),
        new("Goodwill Trading", "Other", "Neutral trading group founded in November 2016 to collect and sell books, maps, banners, and other unique items. Its broader associates community connected traders, map makers, collectors, and friends without treating every participant as a full member.", Wiki + "Goodwill_Trading", "#9b7854", "https://static.wikitide.net/2b2twiki/3/34/GoodwillTradingBanner.png", "November 20, 2016", "Inactive", Locations: ["Acheron"]),
        new("JIDF", "Build", "Spawn-base group created by Joey_Coconut in 2020. It built Fort Aqua and later helped revitalize the Shortbus Caliphate before merging into it.", Wiki + "JIDF", "#4f86a8", "https://static.wikitide.net/2b2twiki/2/25/JIDF.jpg", "2020", "Merged into Shortbus Caliphate", Locations: ["Fort Aqua"]),
        new("The Enclave", "Mixed", "Builder and griefer group founded in May 2024. Its documented bases include GoodSprings, Port Eden, New Eden, Anthem, Montpellier, and several spawn projects.", Wiki + "Enclave", "#5d7459", "https://static.wikitide.net/2b2twiki/b/b6/Enclave_logo.png", "May 2, 2024", "Active", Locations: ["Anthem"]),
        new("The Legion of Shenandoah", "Build", "Early building group formed by members of Shenandoah in 2013-2014. Its documented bases include Mjolnir, Sleepy's Hollow, Shenandoah, and Asgard II.", Wiki + "The_Legion_of_Shenandoah", "#65708f", "https://static.wikitide.net/2b2twiki/2/2a/333_converted.png", "Summer/Fall 2013", "Disbanded", Locations: ["Asgard 2", "Shenendoah", "Sleepy's Hollow"]),
        new("The Society Project", "Other", "Research and archival group founded on June 6, 2017. Also known as The Society, it documents 2b2t players and history, produces world downloads and renders, conducts community research, and investigates methods of hiding bases near spawn. Its documented bases include Thotsburg and Newmelon Hamlet.", Wiki + "The_Society", "#758698", "https://static.wikitide.net/2b2twiki/1/1f/Society-logo.png", "June 6, 2017", "Active", Aliases: ["The Society"], Locations: ["Thotsburg", "Newmelon Hamlet"]),
        new("United States of Hakle", "Build", "Spawn building group formed during the 2024 United States election. Its documented projects include The Outpost, Hakleville, and Haklegrad.", Wiki + "UnitedStatesofHakle", "#9a6878", "https://static.wikitide.net/2b2twiki/9/9c/Outpost.png", "November 5, 2024", "Active", Locations: ["The Outpost"]),

        // ---- Highway / infrastructure groups ----
        new("The Highway Patrol", "Highway", "Highway-focused group recorded in the Atlas for road maintenance and patrol work. Its detailed history and verified emblem have not yet been documented in the available public sources.", null, "#e06666", null, null, "Unknown"),
        new("Highway Workers Union (HWU)", "Highway", "Nether infrastructure group founded in September 2020. HWU succeeded much of IIS's work and uses automated building tools to pave, extend, clear, and signpost the eight principal Nether highways.", Wiki + "Highway_Workers_Union", "#f1c232", "https://static.wikitide.net/2b2twiki/thumb/e/e6/HWU_Logo.png/512px-HWU_Logo.png", "September 14, 2020", "Active", DiscordUrl: "https://discord.gg/QrJsP6FuJB"),
        new("Nether Highway Group (NHG)", "Highway", "Early border expedition and highway group formed in late 2016. NHG completed the eastern Nether highway to the world border, continued toward the ++ corner, and built Point Dory and numerous milestones along the route.", Wiki + "Nether_Highway_Group", "#ff7b00", "https://static.wikitide.net/2b2twiki/e/e3/NHGBanner.png", "December 2016", "Disbanded / hiatus", Locations: ["Point Dory", "Point Nemo", "Mu Megabase"]),
        new("Spawn Infrastructure Group (SIG)", "Mixed", "Infrastructure group founded in November 2017 that repaired Nether highways and built Overworld amenities including outposts and Southern Canal works. SIG became a VoCo subgroup and ended when VoCo disbanded in March 2021.", Wiki + "Spawn_Infrastructure_Group", "#45818e", "https://static.wikitide.net/2b2twiki/1/1b/SIGLogo.jpg", "November 18, 2017", "Disbanded", Aliases: ["SIG"]),
        new("Independent Interstate Society (IIS)", "Highway", "Nether highway group active from July 2019 to November 2020. IIS popularized large-scale Baritone and Schematica-assisted road building, expanded the principal routes and ring roads, then largely merged into HWU and MEG.", Wiki + "Independent_Interstate_Society", "#3d85c6", "https://static.wikitide.net/2b2twiki/7/7d/IISlogo_official.jpg", "July 30, 2019", "Disbanded", Aliases: ["IIS"]),
        new("-X Diggers", "Highway", "Short-lived expedition founded by Metrez and Bramblery in November 2017 to dig the western Nether axis highway to the Overworld world border, a goal completed the following month.", Wiki + "-X_Diggers", "#a64d79", "https://static.wikitide.net/2b2twiki/a/a3/-XWRDBORDER.jpg", "November 6, 2017", "Inactive"),
        new("Motorway Extension Gurus (MEG)", "Highway", "Nether highway mining group founded in October 2019. MEG widened and extended long-distance routes, including the diagonal highways to the world border, with tunnel standards designed for fast cheat-assisted travel.", Wiki + "Motorway_Extension_Gurus", "#674ea7", "https://static.wikitide.net/2b2twiki/thumb/1/1c/MEGNewLogo.png/512px-MEGNewLogo.png", "October 15, 2019", "Inactive", DiscordUrl: "https://discord.gg/UhjJNQ9fBW", Aliases: ["MEG"]),
        new("+Z Digging Group", "Highway", "Axis-highway crew formed around late 2016 after work began on the southern Nether highway. The group completed the +Z route to the Overworld world border on June 9, 2017 and built milestones and small bases along it.", Wiki + "%2BZ_Digging_Group", "#2986cc", "https://static.wikitide.net/2b2twiki/thumb/6/60/Z%2B_Diggers.png/512px-Z%2B_Diggers.png", "Late 2016 / early 2017", "Inactive"),
        new("Southern Canal Association", "Highway", "Canal infrastructure group founded in August 2022 to extend and maintain the Southern Canal. By its dissolution in February 2023 the canal reached roughly 540,000 blocks, and most members continued the work in WaterWay Union.", Wiki + "Southern_Canal_Association", "#16a085", "https://static.wikitide.net/2b2twiki/thumb/7/7c/SCA_logo_2.png/512px-SCA_logo_2.png", "August 16, 2022", "Inactive"),
        new("WaterWay Union", "Highway", "Infrastructure group founded in January 2023 to expand and maintain the +Z Southern Canal, continuing work begun by the Southern Canal Association and building canal outposts and amenities along the route.", Wiki + "WaterWay_Union", "#0b8043", "https://static.wikitide.net/2b2twiki/3/38/New_Canal.png", "January 2023", "Active", Locations: ["Cookie Shack", "Cookie Shack 2", "Cookie Shack 4", "Cookie Shack 7"]),

        // ---- Historically significant (PvP / community) ----
        new("Team Rusher", "Other", "Large, decentralized 2016 faction associated with TheCampingRusher's audience. It was one of the two principal sides of the Rusher War and opposed Team Veteran during the influx of new players that reshaped 2b2t.", Wiki + "Team_Rusher", "#990000", "https://static.wikitide.net/2b2twiki/6/61/RusherFlag-0.png", "June 1, 2016", "Inactive"),
        new("Facepunch Republic", "Other", "Early 2b2t faction formed by players from the Facepunch forum in April 2011. At its height it organized hundreds of players, fought the server's 4chan population, and established a network of bases and outposts before collapsing in 2012.", Wiki + "Facepunch_Republic", "#741b47", "https://static.wikitide.net/2b2twiki/5/59/11.png", "April 27, 2011", "Inactive", Locations: ["Camp Facepunch", "2k2k"]),
        new("Headpats4All", "Other", "Social group founded by Triibu and Nr1Princess in September 2019. It maintained several themed bases and projects and was described by the wiki as semi-active.", Wiki + "Headpats4All", "#ef86ae", "https://static.wikitide.net/2b2twiki/7/76/H4ABanner.png", "September 12, 2019", "Semi-active", Aliases: ["H4A"], Locations: ["Point Pat"]),
        new("Infinity Incursion", "Other", "Incursion group founded by BachiBachBach in August 2019. It organized large spawn projects including Operation Black Sky and the 2020 water cube, while its building division produced several documented bases before the group disbanded in 2021.", Wiki + "Infinity_Incursion", "#6446a8", "https://static.wikitide.net/2b2twiki/2/23/Infinity_Incursion_Symbol.png", "August 11, 2019", "Disbanded", Locations: ["George Harbor", "Spook Base III", "Spook Base 4", "Waternoose"]),
        new("New Facepunch Republic", "Other", "2019 revival of the early Facepunch Republic led by returning founder chezhead. It focused on building and restoration projects, including the 2k2k rebuild and Fort Aqua, before becoming inactive in 2021.", Wiki + "New_Facepunch_Republic", "#8d3f66", "https://static.wikitide.net/2b2twiki/4/48/NFPR.png", "November 20, 2019", "Inactive", Locations: ["2k2k", "Fort Aqua"], LocationRole: "Builder / restorer"),
        new("Peacekeepers", "Other", "Rusher-era faction founded in July 2016 as an attempted neutral third party between Team Rusher and Team Veteran. It later aligned with Team Rusher before eventually disbanding.", Wiki + "Peacekeepers", "#6596b8", "https://static.wikitide.net/2b2twiki/f/f6/Imageedit_10_8367309629.png", "July 8, 2016", "Disbanded", Aliases: ["PK"], Locations: ["Atlantis", "Peacekeeper HQ"]),
        new("The Gulag", "Other", "Social and conflict-oriented group founded by c0mmie_ in late 2018. Its projects included several distinctive spawn constructions, most notably c0mmiegrad.", Wiki + "The_Gulag", "#a23b3b", "https://static.wikitide.net/2b2twiki/b/bf/GOOLAG_0_0.png", "Late 2018", "Active", Locations: ["c0mmiegrad"]),
        new("The Watchmen", "Other", "Faction founded in October 2020 and best known for its obsidian OwO spawn project. The group also established the Aura spawn base before disbanding.", Wiki + "Watchmen", "#d4a241", "https://static.wikitide.net/2b2twiki/7/7e/The_Watchmen_Logo_.gif", "October 22, 2020", "Disbanded", Aliases: ["Watchmen"], Locations: ["Aura"]),
    };

    /// <summary>Adds missing canonical 2b2t groups and refreshes metadata owned by the seed catalog.</summary>
    /// <returns>A task that completes after required group changes are persisted.</returns>
    /// <remarks>Administrator-created groups outside the canonical name set are never modified.</remarks>
    public async Task SeedAsync()
    {
        var existing = await _context.Groups.ToListAsync();
        var byName = existing.ToDictionary(g => g.Name, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("o");

        int added = 0, updated = 0;
        foreach (var c in Canonical)
        {
            if (!byName.TryGetValue(c.Name, out var row) && c.Aliases is not null)
            {
                row = c.Aliases.Select(alias => byName.GetValueOrDefault(alias)).FirstOrDefault(candidate => candidate is not null);
                if (row is not null)
                {
                    byName.Remove(row.Name);
                    row.Name = c.Name;
                    byName[c.Name] = row;
                }
            }

            if (row is not null)
            {
                // The public wiki moved to WikiOasis while its revision API remained on the
                // former Miraheze host. Migrate only the exact old canonical host; arbitrary
                // administrator-selected sources remain authoritative.
                if (!string.IsNullOrWhiteSpace(c.Wiki) &&
                    row.WikiUrl is not null &&
                    row.WikiUrl.StartsWith("https://2b2t.miraheze.org/wiki/", StringComparison.OrdinalIgnoreCase) &&
                    c.Wiki.StartsWith(Wiki, StringComparison.OrdinalIgnoreCase))
                {
                    row.WikiUrl = c.Wiki;
                    row.ModifiedUtc = now;
                    updated++;
                }

                // Replace only Atlas's old MediaWiki redirect-form logos. Direct uploads,
                // operator-hosted art, and every other administrator override are preserved.
                if (!string.IsNullOrWhiteSpace(c.LogoUrl) &&
                    row.LogoUrl is not null &&
                    row.LogoUrl.StartsWith("https://2b2t.miraheze.org/wiki/Special:Redirect/file/", StringComparison.OrdinalIgnoreCase))
                {
                    row.LogoUrl = c.LogoUrl;
                    row.LogoSourceUrl = c.Wiki ?? row.LogoSourceUrl;
                    row.ModifiedUtc = now;
                    updated++;
                }

                // The old SpawnMasons vanity invite was later reassigned to the distinct
                // Imperials of 2b2t server. Remove only the exact value previously seeded by
                // Atlas so a current Imperials invite is never presented as a Mason contact.
                if (string.Equals(c.Name, "SpawnMasons", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(row.DiscordUrl, "https://discord.gg/spawnmasons", StringComparison.OrdinalIgnoreCase))
                {
                    row.DiscordUrl = null;
                    row.ModifiedUtc = now;
                    updated++;
                }

                // Existing pre-enrichment rows have none of these fields. Backfill
                // the reviewed canonical record once, then preserve admin changes.
                var needsEnrichmentBackfill = string.IsNullOrWhiteSpace(row.LogoSourceUrl) &&
                    string.IsNullOrWhiteSpace(row.Founded) &&
                    string.IsNullOrWhiteSpace(row.Status);
                if (needsEnrichmentBackfill)
                {
                    row.Type = c.Type;
                    row.Description = c.Desc;
                    row.WikiUrl = c.Wiki;
                    row.Color = c.Color;
                    row.WebsiteUrl = c.WebsiteUrl;
                    row.DiscordUrl = c.DiscordUrl;
                    row.LogoUrl = c.LogoUrl;
                    row.LogoSourceUrl = c.Wiki ?? c.WebsiteUrl;
                    row.Founded = c.Founded;
                    row.Status = c.Status;
                    row.ModifiedUtc = now;
                    updated++;
                }
                else
                {
                    // Canonical records discovered by the evidence audit can already have a logo,
                    // source, founding date, and status while their prose/color fields are blank.
                    // Fill only those blank fields; any text or color chosen by an administrator wins.
                    var metadataAdded = false;
                    if (string.IsNullOrWhiteSpace(row.Description) && !string.IsNullOrWhiteSpace(c.Desc))
                    {
                        row.Description = c.Desc;
                        metadataAdded = true;
                    }
                    if (string.IsNullOrWhiteSpace(row.Color) && !string.IsNullOrWhiteSpace(c.Color))
                    {
                        row.Color = c.Color;
                        metadataAdded = true;
                    }
                    if (metadataAdded)
                    {
                        row.ModifiedUtc = now;
                        updated++;
                    }

                    // The evidence audit may verify structured metadata after a group has
                    // already been reviewed. Fill only missing values so operator-entered
                    // URLs, artwork, dates, and status always remain authoritative.
                    var structuredMetadataAdded = false;
                    if (string.IsNullOrWhiteSpace(row.WikiUrl) && !string.IsNullOrWhiteSpace(c.Wiki))
                    {
                        row.WikiUrl = c.Wiki;
                        structuredMetadataAdded = true;
                    }
                    if (string.IsNullOrWhiteSpace(row.LogoUrl) && !string.IsNullOrWhiteSpace(c.LogoUrl))
                    {
                        row.LogoUrl = c.LogoUrl;
                        structuredMetadataAdded = true;
                    }
                    if (string.IsNullOrWhiteSpace(row.LogoSourceUrl))
                    {
                        var canonicalLogoSource = c.Wiki ?? c.WebsiteUrl;
                        if (!string.IsNullOrWhiteSpace(canonicalLogoSource))
                        {
                            row.LogoSourceUrl = canonicalLogoSource;
                            structuredMetadataAdded = true;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(row.Founded) && !string.IsNullOrWhiteSpace(c.Founded))
                    {
                        row.Founded = c.Founded;
                        structuredMetadataAdded = true;
                    }
                    if (string.IsNullOrWhiteSpace(row.Status) && !string.IsNullOrWhiteSpace(c.Status))
                    {
                        row.Status = c.Status;
                        structuredMetadataAdded = true;
                    }
                    if (structuredMetadataAdded)
                    {
                        row.ModifiedUtc = now;
                        updated++;
                    }

                    // One-time reviewed classification corrections: both groups have broad
                    // faction histories, but their Atlas-linked output is predominantly bases.
                    if ((string.Equals(c.Name, "The Emperium", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(c.Name, "The Imperials", StringComparison.OrdinalIgnoreCase)) &&
                        string.Equals(row.Type, "Other", StringComparison.OrdinalIgnoreCase))
                    {
                        row.Type = "Build";
                        row.Description = c.Desc;
                        row.Founded = c.Founded;
                        row.ModifiedUtc = now;
                        updated++;
                    }

                    // Add newly verified public contact points without overwriting an operator's choice.
                    // Dead or historical sites stay null rather than being presented as active resources.
                    var contactAdded = false;
                    if (string.IsNullOrWhiteSpace(row.WebsiteUrl) && !string.IsNullOrWhiteSpace(c.WebsiteUrl))
                    {
                        row.WebsiteUrl = c.WebsiteUrl;
                        contactAdded = true;
                    }
                    if (string.IsNullOrWhiteSpace(row.DiscordUrl) && !string.IsNullOrWhiteSpace(c.DiscordUrl))
                    {
                        row.DiscordUrl = c.DiscordUrl;
                        contactAdded = true;
                    }
                    if (contactAdded)
                    {
                        row.ModifiedUtc = now;
                        updated++;
                    }
                }
            }
            else
            {
                _context.Groups.Add(new Group
                {
                    Name = c.Name,
                    Type = c.Type,
                    Description = c.Desc,
                    WikiUrl = c.Wiki,
                    Color = c.Color,
                    WebsiteUrl = c.WebsiteUrl,
                    DiscordUrl = c.DiscordUrl,
                    LogoUrl = c.LogoUrl,
                    LogoSourceUrl = c.Wiki ?? c.WebsiteUrl,
                    Founded = c.Founded,
                    Status = c.Status,
                    DateAddedUtc = now,
                    ModifiedUtc = now,
                });
                added++;
            }
        }

        if (added > 0 || updated > 0)
        {
            await _context.SaveChangesAsync();
        }

        var canonicalRows = await _context.Groups.ToListAsync();
        var canonicalByName = canonicalRows.ToDictionary(group => group.Name, StringComparer.OrdinalIgnoreCase);
        var locations = await _context.Locations.AsNoTracking().ToListAsync();
        var locationByName = locations
            .GroupBy(location => location.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var existingLinks = (await _context.LocationGroups.AsNoTracking().ToListAsync())
            .Select(link => (link.LocationRowid, link.GroupId))
            .ToHashSet();
        var linked = 0;
        foreach (var canonical in Canonical.Where(item => item.Locations is { Length: > 0 }))
        {
            if (!canonicalByName.TryGetValue(canonical.Name, out var group)) continue;
            foreach (var locationName in canonical.Locations!)
            {
                if (!locationByName.TryGetValue(locationName, out var location) ||
                    !existingLinks.Add((location.Rowid, group.Id))) continue;
                _context.LocationGroups.Add(new LocationGroup
                {
                    LocationRowid = location.Rowid,
                    GroupId = group.Id,
                    Role = canonical.LocationRole,
                    DateAddedUtc = now,
                });
                linked++;
            }
        }

        // Reuse the same rules that run during ingestion and enrichment; startup also repairs
        // older rows without waiting for wiki indexing or a model response.
        if (linked > 0) await _context.SaveChangesAsync();
        linked += await new ArchiveGroupAttributionService(_context).StageAsync();
        if (linked > 0) await _context.SaveChangesAsync();

        _logger.LogInformation(
            "Group seed: {Added} added, {Updated} legacy row(s) enriched, {Linked} location attribution(s) added ({Total} canonical).",
            added, updated, linked, Canonical.Length);
    }
}
