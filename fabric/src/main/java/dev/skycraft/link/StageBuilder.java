package dev.skycraft.link;

import com.mojang.brigadier.arguments.DoubleArgumentType;
import com.mojang.brigadier.arguments.FloatArgumentType;
import com.mojang.brigadier.arguments.IntegerArgumentType;
import com.mojang.brigadier.arguments.LongArgumentType;
import com.mojang.brigadier.arguments.StringArgumentType;
import com.mojang.brigadier.context.CommandContext;
import dev.skycraft.SkyCraft;
import dev.skycraft.world.PlacedBlocks;
import java.util.HashMap;
import java.util.Map;
import java.util.UUID;
import java.util.function.BiPredicate;
import net.fabricmc.fabric.api.command.v2.CommandRegistrationCallback;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.commands.arguments.IdentifierArgument;
import net.minecraft.core.BlockPos;
import net.minecraft.core.Direction;
import net.minecraft.core.registries.Registries;
import net.minecraft.network.chat.Component;
import net.minecraft.resources.Identifier;
import net.minecraft.resources.ResourceKey;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerBossEvent;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.util.RandomSource;
import net.minecraft.world.BossEvent;
import net.minecraft.world.RandomizableContainer;
import net.minecraft.world.entity.EntitySpawnReason;
import net.minecraft.world.entity.EntityType;
import net.minecraft.world.entity.ai.attributes.Attributes;
import net.minecraft.world.entity.boss.enderdragon.EndCrystal;
import net.minecraft.world.entity.boss.enderdragon.EnderDragon;
import net.minecraft.world.entity.boss.enderdragon.phases.EnderDragonPhase;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.block.ChestBlock;
import net.minecraft.world.level.block.EndPortalBlock;
import net.minecraft.world.level.block.EndPortalFrameBlock;
import net.minecraft.world.level.block.NetherPortalBlock;
import net.minecraft.world.level.block.Portal;
import net.minecraft.world.level.block.state.BlockState;

/**
 * RiskOfCraft's stages, built in the mirror world from Risk of Rain 2's commands
 * (design/gameplay.json commands): random loot chests, the stage-5 End portal frame, Nether
 * decorations, the End's dragon arena and its Ender Dragon. Everything placed is remembered by
 * PlacedBlocks, so the next stage clears it.
 *
 * Portals: a player in a Nether portal (2 s) or an End portal is not moved to another dimension;
 * Risk of Rain 2 is told (event PortalEntered) and takes the whole run to its custom stage.
 */
public final class StageBuilder {
	public static final int EV_PORTAL_ENTERED = 6; // design/protocol_enums.json EventType
	public static final int EV_DRAGON_KILLED = 7;
	public static final int EV_CREDITS_DONE = 8;
	public static final int EV_SET_SPAWNS = 9;
	public static final int PORTAL_NETHER = 1;
	public static final int PORTAL_END = 2;
	private static final int NETHER_HOLD_TICKS = 40; // design/gameplay.json portals: 2 s
	private static final int PORTAL_COOLDOWN_TICKS = 200;

	/** Set by the client half: sends a block texture to Risk of Rain 2 as render-ring texture id. */
	public static volatile BiPredicate<Integer, String> textureSender = (id, name) -> false;

	private static final Map<UUID, int[]> PORTAL_TICKS = new HashMap<>(); // player -> {ticks inside, last tick, cooldown until}
	private static ServerBossEvent dragonBar;
	private static EnderDragon dragon;

	/** Every entity RiskOfCraft spawns carries both tags; one from an earlier session is removed when its chunk loads. */
	public static final String TAG = "riskofcraft";
	/** A Nether mob of the Nether stage (riskofcraft stage_mob): counted for the cap, cleared at the next stage. */
	public static final String MOB_TAG = "riskofcraft_mob";
	public static final String SESSION_TAG = "riskofcraft_" + Long.toHexString(System.nanoTime() ^ System.currentTimeMillis());
	private static final java.util.List<net.minecraft.world.entity.Entity> STALE = new java.util.ArrayList<>();

	private StageBuilder() {
	}

	public static void init() {
		ServerTickEvents.END_SERVER_TICK.register(StageBuilder::tick);
		net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents.SERVER_STARTED.register(PlacedBlocks::load);
		net.fabricmc.fabric.api.event.lifecycle.v1.ServerLifecycleEvents.SERVER_STOPPING.register(PlacedBlocks::save);
		// A dragon, crystal, villager or display left in the saved world by an earlier session (the
		// dragon roared over the title screen): removed on the next tick, not mid-load.
		net.fabricmc.fabric.api.event.lifecycle.v1.ServerEntityEvents.ENTITY_LOAD.register((entity, level) -> {
			boolean ours = entity instanceof EnderDragon || entity instanceof EndCrystal || entity.entityTags().contains(TAG);
			if (ours && !entity.entityTags().contains(SESSION_TAG)) {
				synchronized (STALE) {
					STALE.add(entity);
				}
			}
		});
		CommandRegistrationCallback.EVENT.register((dispatcher, registry, environment) -> dispatcher.register(
			Commands.literal("riskofcraft")
				.then(Commands.literal("place_chest")
					.then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("table", IdentifierArgument.id()).then(Commands.argument("facing", StringArgumentType.word()).executes(StageBuilder::placeChest)))))))
				.then(Commands.literal("end_portal")
					.then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("eye_chance", FloatArgumentType.floatArg(0.0F, 1.0F)).executes(StageBuilder::endPortal))))))
				.then(Commands.literal("decor")
					.then(Commands.argument("kind", StringArgumentType.word()).then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("seed", LongArgumentType.longArg()).executes(StageBuilder::decor)))))))
				.then(Commands.literal("end_arena")
					.then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("pillars", IntegerArgumentType.integer(0, 32)).then(Commands.argument("radius", DoubleArgumentType.doubleArg(4.0)).then(Commands.argument("caged", IntegerArgumentType.integer(0, 32)).executes(StageBuilder::endArena))))))))
				.then(Commands.literal("summon_dragon")
					.then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("health", FloatArgumentType.floatArg(1.0F)).executes(StageBuilder::summonDragon))))))
				.then(Commands.literal("spawns")
					// Testing: Risk of Rain 2's monster spawns off (the monsters out are cleared) or on.
					.then(Commands.literal("on").executes(ctx -> spawns(ctx, true)))
					.then(Commands.literal("off").executes(ctx -> spawns(ctx, false))))
				.then(Commands.literal("credits").executes(ctx -> {
					// The End Poem and credits, as leaving the End shows them (the player isn't moved: wonGame stays false).
					ServerPlayer player = ctx.getSource().getPlayerOrException();
					player.connection.send(new net.minecraft.network.protocol.game.ClientboundGameEventPacket(
						net.minecraft.network.protocol.game.ClientboundGameEventPacket.WIN_GAME, 1.0F));
					SkyCraft.LOG.info("RiskOfCraft: the End Poem and credits for {}", player.getName().getString());
					return 1;
				}))
				.then(Commands.literal("villager")
					.then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("profession", StringArgumentType.word()).then(Commands.argument("level", IntegerArgumentType.integer(1, 5))
							.then(Commands.argument("type", StringArgumentType.word()).then(Commands.argument("yaw", FloatArgumentType.floatArg()).executes(StageBuilder::villager)))))))))
				.then(Commands.literal("item_display")
					.then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("item", StringArgumentType.word()).executes(StageBuilder::itemDisplay))))))
				.then(Commands.literal("stage_mob")
					.then(Commands.argument("entity", StringArgumentType.word()).then(Commands.argument("x", DoubleArgumentType.doubleArg()).then(Commands.argument("y", DoubleArgumentType.doubleArg()).then(Commands.argument("z", DoubleArgumentType.doubleArg())
						.then(Commands.argument("cap", IntegerArgumentType.integer(0, 64)).then(Commands.argument("health", FloatArgumentType.floatArg(0.1F)).then(Commands.argument("damage", FloatArgumentType.floatArg(0.1F))
							.executes(StageBuilder::stageMob)))))))))
				.then(Commands.literal("clear_mobs").executes(ctx -> {
					int n = 0;
					for (net.minecraft.world.entity.Mob m : ctx.getSource().getLevel().getEntities(net.minecraft.world.level.entity.EntityTypeTest.forClass(net.minecraft.world.entity.Mob.class), m -> m.entityTags().contains(MOB_TAG))) {
						m.discard();
						n++;
					}
					return n;
				}))
				.then(Commands.literal("clear_dragons").executes(ctx -> {
					// Stage cleanup: the last stage's dragons go without dying (a kill would win the run).
					ServerLevel level = ctx.getSource().getLevel();
					int n = 0;
					for (EnderDragon d : level.getEntities(net.minecraft.world.level.entity.EntityTypeTest.forClass(EnderDragon.class), d -> true)) {
						d.discard();
						n++;
					}
					if (dragonBar != null) {
						dragonBar.removeAllPlayers();
						dragonBar = null;
					}
					dragon = null;
					return n;
				}))
				.then(Commands.literal("send_texture")
					.then(Commands.argument("id", IntegerArgumentType.integer(1000)).then(Commands.argument("texture", StringArgumentType.greedyString()).executes(ctx -> {
						int id = IntegerArgumentType.getInteger(ctx, "id");
						String texture = StringArgumentType.getString(ctx, "texture");
						boolean ok = textureSender.test(id, texture);
						SkyCraft.LOG.info("RiskOfCraft: block texture {} for Risk of Rain 2 (id {}) {}", texture, id, ok ? "queued" : "not available");
						return ok ? 1 : 0;
					}))))
		));
	}

	/**
	 * The block standing on Risk of Rain 2's ground at x, y, z (y is the ground's height). The cell
	 * the ground passes through is mostly under RoR2's surface, which hides anything in it, so the
	 * build goes in the first cell above: sunk at most 0.15 into a slope, or just above the ground.
	 */
	private static BlockPos pos(CommandContext<CommandSourceStack> ctx) {
		return BlockPos.containing(DoubleArgumentType.getDouble(ctx, "x"), Math.ceil(DoubleArgumentType.getDouble(ctx, "y") - 0.15), DoubleArgumentType.getDouble(ctx, "z"));
	}

	private static void set(ServerLevel level, BlockPos pos, BlockState state) {
		level.setBlock(pos, state, 3);
		PlacedBlocks.remember(pos);
	}

	// ---- portals and the dragon ----------------------------------------------------------------

	/** A player is inside a portal block this tick. True: Minecraft must not move them. */
	public static boolean portal(ServerPlayer player, Portal portal) {
		int kind = portal instanceof NetherPortalBlock ? PORTAL_NETHER : portal instanceof EndPortalBlock ? PORTAL_END : 0;
		if (kind == 0) {
			return false;
		}
		int now = player.level().getServer().getTickCount();
		int[] t = PORTAL_TICKS.computeIfAbsent(player.getUUID(), k -> new int[3]);
		if (now < t[2]) {
			return true;
		}
		t[0] = now - t[1] <= 2 ? t[0] + (now - t[1]) : 0; // consecutive ticks inside
		t[1] = now;
		if (kind == PORTAL_NETHER && t[0] < NETHER_HOLD_TICKS) {
			return true;
		}
		t[0] = 0;
		t[2] = now + PORTAL_COOLDOWN_TICKS;
		SkyLink.pushEvent(EV_PORTAL_ENTERED, kind, 0.0F, 0.0F, 0.0F, 0.0F, 0);
		SkyCraft.LOG.info("RiskOfCraft: {} stepped into the {} portal", player.getName().getString(), kind == PORTAL_NETHER ? "Nether" : "End");
		return true;
	}

	public static void dragonDied(EnderDragon dead) {
		if (dead.level().isClientSide()) {
			return;
		}
		SkyLink.pushEvent(EV_DRAGON_KILLED, 0, 0.0F, 0.0F, 0.0F, 0.0F, 0);
		SkyCraft.LOG.info("RiskOfCraft: the Ender Dragon died");
		if (dragonBar != null) {
			dragonBar.setProgress(0.0F);
		}
	}

	private static int spawns(CommandContext<CommandSourceStack> ctx, boolean on) {
		SkyLink.pushEvent(EV_SET_SPAWNS, on ? 1 : 0, 0.0F, 0.0F, 0.0F, 0.0F, 0);
		ctx.getSource().sendSuccess(() -> Component.literal("Risk of Rain 2 monster spawns " + (on ? "on" : "off") + " (testing)"), false);
		return 1;
	}

	/** The credits screen closed (client thread). */
	public static void creditsDone() {
		SkyLink.pushEvent(EV_CREDITS_DONE, 0, 0.0F, 0.0F, 0.0F, 0.0F, 0);
		SkyCraft.LOG.info("RiskOfCraft: credits over");
	}

	private static void tick(MinecraftServer server) {
		synchronized (STALE) {
			if (!STALE.isEmpty()) {
				for (net.minecraft.world.entity.Entity e : STALE) {
					if (!e.isRemoved()) {
						e.discard();
					}
				}
				SkyCraft.LOG.info("RiskOfCraft: removed {} entities left from an earlier session", STALE.size());
				STALE.clear();
			}
		}
		if (server.getTickCount() % 200 == 0) {
			PlacedBlocks.save(server);
		}
		if (dragonBar == null) {
			return;
		}
		if (dragon == null || dragon.isRemoved()) {
			dragonBar.removeAllPlayers();
			dragonBar = null;
			dragon = null;
			return;
		}
		dragonBar.setProgress(Math.max(0.0F, dragon.getHealth() / dragon.getMaxHealth()));
		for (ServerPlayer player : server.getPlayerList().getPlayers()) {
			if (!dragonBar.getPlayers().contains(player)) {
				dragonBar.addPlayer(player);
			}
		}
	}

	private static int summonDragon(CommandContext<CommandSourceStack> ctx) {
		ServerLevel level = ctx.getSource().getLevel();
		BlockPos origin = pos(ctx);
		float health = FloatArgumentType.getFloat(ctx, "health");
		EnderDragon made = net.minecraft.world.entity.EntityTypes.ENDER_DRAGON.create(level, EntitySpawnReason.COMMAND);
		if (made == null) {
			return 0;
		}
		made.snapTo(origin.getX() + 0.5, origin.getY() + 40.0, origin.getZ() + 0.5);
		made.setFightOrigin(origin);
		var maxHealth = made.getAttribute(Attributes.MAX_HEALTH);
		if (maxHealth != null) {
			maxHealth.setBaseValue(health);
		}
		made.setHealth(health);
		made.getPhaseManager().setPhase(EnderDragonPhase.HOLDING_PATTERN);
		made.addTag(TAG);
		made.addTag(SESSION_TAG);
		dragon = made;
		level.addFreshEntity(made);
		if (dragonBar != null) {
			dragonBar.removeAllPlayers();
		}
		dragonBar = new ServerBossEvent(UUID.randomUUID(), Component.translatable("entity.minecraft.ender_dragon"), BossEvent.BossBarColor.PINK, BossEvent.BossBarOverlay.PROGRESS);
		dragonBar.setPlayBossMusic(true);
		dragonBar.setCreateWorldFog(true);
		SkyCraft.LOG.info("RiskOfCraft: Ender Dragon summoned over {} with {} health", origin, health);
		return 1;
	}

	// ---- the Nether's mobs ------------------------------------------------------------------------

	/**
	 * One of Minecraft's Nether mobs near the player on the Nether stage, unless the cap of them is
	 * already about. Health and attack damage are multiplied for the run's difficulty; piglins and
	 * hoglins don't turn into zombies (the mirror world is the overworld).
	 */
	private static int stageMob(CommandContext<CommandSourceStack> ctx) throws com.mojang.brigadier.exceptions.CommandSyntaxException {
		ServerLevel level = ctx.getSource().getLevel();
		ServerPlayer player = ctx.getSource().getPlayerOrException();
		int cap = IntegerArgumentType.getInteger(ctx, "cap");
		int alive = level.getEntities(net.minecraft.world.level.entity.EntityTypeTest.forClass(net.minecraft.world.entity.Mob.class),
			m -> m.entityTags().contains(MOB_TAG) && m.distanceToSqr(player) < 80.0 * 80.0).size();
		if (alive >= cap) {
			return 0;
		}
		Identifier id = Identifier.withDefaultNamespace(StringArgumentType.getString(ctx, "entity"));
		if (!net.minecraft.core.registries.BuiltInRegistries.ENTITY_TYPE.containsKey(id)) {
			SkyCraft.LOG.warn("RiskOfCraft: no entity {}", id);
			return 0;
		}
		EntityType<?> type = net.minecraft.core.registries.BuiltInRegistries.ENTITY_TYPE.getValue(id);
		float health = FloatArgumentType.getFloat(ctx, "health");
		float damage = FloatArgumentType.getFloat(ctx, "damage");
		BlockPos at = BlockPos.containing(DoubleArgumentType.getDouble(ctx, "x"), DoubleArgumentType.getDouble(ctx, "y"), DoubleArgumentType.getDouble(ctx, "z"));
		net.minecraft.world.entity.Entity made = type.spawn(level, e -> {
			e.addTag(TAG);
			e.addTag(SESSION_TAG);
			e.addTag(MOB_TAG);
			if (e instanceof net.minecraft.world.entity.monster.piglin.AbstractPiglin piglin) {
				piglin.setImmuneToZombification(true);
			}
			if (e instanceof net.minecraft.world.entity.monster.hoglin.Hoglin hoglin) {
				hoglin.setImmuneToZombification(true);
			}
		}, at, EntitySpawnReason.MOB_SUMMONED, false, false);
		if (!(made instanceof net.minecraft.world.entity.Mob mob)) {
			return 0;
		}
		scale(mob, Attributes.MAX_HEALTH, health);
		scale(mob, Attributes.ATTACK_DAMAGE, damage);
		mob.setHealth(mob.getMaxHealth());
		mob.setPersistenceRequired(); // stays until the stage ends (clear_mobs), not despawned by distance
		return 1;
	}

	private static void scale(net.minecraft.world.entity.Mob mob, net.minecraft.core.Holder<net.minecraft.world.entity.ai.attributes.Attribute> attribute, float factor) {
		var instance = mob.getAttribute(attribute);
		if (instance == null || factor == 1.0F) {
			return;
		}
		Identifier id = Identifier.fromNamespaceAndPath("riskofcraft", "difficulty");
		instance.removeModifier(id);
		instance.addPermanentModifier(new net.minecraft.world.entity.ai.attributes.AttributeModifier(id, factor - 1.0,
			net.minecraft.world.entity.ai.attributes.AttributeModifier.Operation.ADD_MULTIPLIED_TOTAL));
	}

	// ---- the Bazaar ------------------------------------------------------------------------------

	/** A trader by the Bazaar counter: no AI, can't be hurt, faces the counter. (Too long for the command ring as a summon.) */
	private static int villager(CommandContext<CommandSourceStack> ctx) {
		String nbt = String.format(java.util.Locale.ROOT,
			"{VillagerData:{profession:\"minecraft:%s\",level:%d,type:\"minecraft:%s\"},NoAI:1b,Invulnerable:1b,PersistenceRequired:1b,Silent:1b,Rotation:[%.1ff,0f],Tags:[\"%s\",\"%s\"]}",
			StringArgumentType.getString(ctx, "profession"), IntegerArgumentType.getInteger(ctx, "level"), StringArgumentType.getString(ctx, "type"), FloatArgumentType.getFloat(ctx, "yaw"), TAG, SESSION_TAG);
		return summon(ctx, "minecraft:villager", DoubleArgumentType.getDouble(ctx, "y"), nbt);
	}

	/** An item floating over a Bazaar terminal, turning to face the player. */
	private static int itemDisplay(CommandContext<CommandSourceStack> ctx) {
		String nbt = String.format(java.util.Locale.ROOT,
			"{item:{id:\"minecraft:%s\",count:1},billboard:\"vertical\",transformation:{left_rotation:[0f,0f,0f,1f],right_rotation:[0f,0f,0f,1f],translation:[0f,0f,0f],scale:[0.8f,0.8f,0.8f]},Tags:[\"%s\",\"%s\"]}",
			StringArgumentType.getString(ctx, "item"), TAG, SESSION_TAG);
		return summon(ctx, "minecraft:item_display", DoubleArgumentType.getDouble(ctx, "y"), nbt);
	}

	private static int summon(CommandContext<CommandSourceStack> ctx, String type, double y, String nbt) {
		String command = String.format(java.util.Locale.ROOT, "summon %s %.3f %.3f %.3f %s", type,
			DoubleArgumentType.getDouble(ctx, "x"), y, DoubleArgumentType.getDouble(ctx, "z"), nbt);
		ctx.getSource().getServer().getCommands().performPrefixedCommand(ctx.getSource(), command);
		return 1;
	}

	// ---- chests and the End portal frame -------------------------------------------------------

	private static int placeChest(CommandContext<CommandSourceStack> ctx) {
		ServerLevel level = ctx.getSource().getLevel();
		BlockPos at = pos(ctx);
		Direction facing = Direction.byName(StringArgumentType.getString(ctx, "facing"));
		if (facing == null || facing.getAxis().isVertical()) {
			facing = Direction.NORTH;
		}
		Identifier table = IdentifierArgument.getId(ctx, "table");
		set(level, at, Blocks.CHEST.defaultBlockState().setValue(ChestBlock.FACING, facing));
		if (level.getBlockEntity(at) instanceof RandomizableContainer chest) {
			chest.setLootTable(ResourceKey.create(Registries.LOOT_TABLE, table), level.getRandom().nextLong());
		}
		return 1;
	}

	/** A stronghold portal room's portal: 12 frames on a stone brick floor, some with eyes (no lava pool under it). */
	private static int endPortal(CommandContext<CommandSourceStack> ctx) {
		ServerLevel level = ctx.getSource().getLevel();
		BlockPos c = pos(ctx);
		float eyeChance = FloatArgumentType.getFloat(ctx, "eye_chance");
		RandomSource random = level.getRandom();
		for (int dx = -2; dx <= 2; dx++) {
			for (int dz = -2; dz <= 2; dz++) {
				set(level, c.offset(dx, -1, dz), Blocks.STONE_BRICKS.defaultBlockState());
				set(level, c.offset(dx, -2, dz), Blocks.STONE_BRICKS.defaultBlockState());
			}
		}
		int eyes = 0;
		for (int i = -1; i <= 1; i++) {
			// Each side's frames face into the portal.
			eyes += frame(level, c.offset(i, 0, -2), Direction.SOUTH, random, eyeChance);
			eyes += frame(level, c.offset(i, 0, 2), Direction.NORTH, random, eyeChance);
			eyes += frame(level, c.offset(-2, 0, i), Direction.EAST, random, eyeChance);
			eyes += frame(level, c.offset(2, 0, i), Direction.WEST, random, eyeChance);
		}
		// Minecraft only opens a portal when an eye is placed: a frame that rolls all 12 is opened here.
		BlockState well = eyes == 12 ? Blocks.END_PORTAL.defaultBlockState() : Blocks.AIR.defaultBlockState();
		for (int dx = -1; dx <= 1; dx++) {
			for (int dz = -1; dz <= 1; dz++) {
				set(level, c.offset(dx, 0, dz), well);
			}
		}
		SkyCraft.LOG.info("RiskOfCraft: End portal frame at {} with {} of 12 eyes", c, eyes);
		return 1;
	}

	private static int frame(ServerLevel level, BlockPos at, Direction facing, RandomSource random, float eyeChance) {
		boolean eye = random.nextFloat() < eyeChance;
		set(level, at, Blocks.END_PORTAL_FRAME.defaultBlockState().setValue(EndPortalFrameBlock.FACING, facing).setValue(EndPortalFrameBlock.HAS_EYE, eye));
		return eye ? 1 : 0;
	}

	// ---- decorations ---------------------------------------------------------------------------

	private static int decor(CommandContext<CommandSourceStack> ctx) {
		ServerLevel level = ctx.getSource().getLevel();
		BlockPos at = pos(ctx);
		String kind = StringArgumentType.getString(ctx, "kind");
		RandomSource random = RandomSource.create(LongArgumentType.getLong(ctx, "seed"));
		if (kind.equals("nether")) {
			switch (random.nextInt(7)) {
				case 0 -> spire(level, at, random);
				case 1 -> fungus(level, at, random);
				case 2 -> fortressArch(level, at, random);
				case 3 -> bastionRubble(level, at, random);
				case 4 -> ruinedPortal(level, at, random);
				case 5 -> basaltColumns(level, at, random);
				default -> soulPatch(level, at, random);
			}
		}
		return 1;
	}

	private static void spire(ServerLevel level, BlockPos at, RandomSource random) {
		int h = 3 + random.nextInt(5);
		for (int y = 0; y < h; y++) {
			set(level, at.above(y), y == 0 ? Blocks.MAGMA_BLOCK.defaultBlockState() : random.nextInt(6) == 0 ? Blocks.NETHER_QUARTZ_ORE.defaultBlockState()
				: random.nextInt(9) == 0 ? Blocks.NETHER_GOLD_ORE.defaultBlockState() : Blocks.NETHERRACK.defaultBlockState());
			if (y < h / 2) {
				for (Direction d : Direction.Plane.HORIZONTAL) {
					if (random.nextInt(3) == 0) {
						set(level, at.above(y).relative(d), Blocks.NETHERRACK.defaultBlockState());
					}
				}
			}
		}
		set(level, at.above(h), Blocks.GLOWSTONE.defaultBlockState());
		if (random.nextInt(20) == 0) {
			set(level, at.relative(Direction.EAST), Blocks.ANCIENT_DEBRIS.defaultBlockState());
		}
	}

	private static void fungus(ServerLevel level, BlockPos at, RandomSource random) {
		boolean warped = random.nextInt(3) == 0;
		BlockState stem = (warped ? Blocks.WARPED_STEM : Blocks.CRIMSON_STEM).defaultBlockState();
		BlockState wart = (warped ? Blocks.WARPED_WART_BLOCK : Blocks.NETHER_WART_BLOCK).defaultBlockState();
		set(level, at, (warped ? Blocks.WARPED_NYLIUM : Blocks.CRIMSON_NYLIUM).defaultBlockState());
		int h = 4 + random.nextInt(4);
		for (int y = 1; y <= h; y++) {
			set(level, at.above(y), stem);
		}
		for (int dx = -2; dx <= 2; dx++) {
			for (int dz = -2; dz <= 2; dz++) {
				for (int dy = 0; dy <= 1; dy++) {
					if (Math.abs(dx) + Math.abs(dz) + dy * 2 <= 3 && !(dx == 0 && dz == 0 && dy == 0)) {
						set(level, at.offset(dx, h - dy + 1, dz), random.nextInt(9) == 0 ? Blocks.SHROOMLIGHT.defaultBlockState() : wart);
					}
				}
			}
		}
	}

	private static void fortressArch(ServerLevel level, BlockPos at, RandomSource random) {
		BlockState bricks = Blocks.NETHER_BRICKS.defaultBlockState();
		Direction along = random.nextBoolean() ? Direction.EAST : Direction.SOUTH;
		for (int i = -3; i <= 3; i++) {
			BlockPos p = at.relative(along, i);
			if (Math.abs(i) == 3) {
				for (int y = 0; y < 5; y++) {
					set(level, p.above(y), bricks);
				}
			}
			set(level, p.above(5), bricks);
			set(level, p.above(6), Blocks.NETHER_BRICK_FENCE.defaultBlockState());
		}
		set(level, at.above(4), Blocks.NETHER_BRICK_FENCE.defaultBlockState());
		if (random.nextInt(3) == 0) {
			set(level, at.relative(along.getClockWise(), 2), Blocks.CHEST.defaultBlockState().setValue(ChestBlock.FACING, along.getCounterClockWise()));
			if (level.getBlockEntity(at.relative(along.getClockWise(), 2)) instanceof RandomizableContainer chest) {
				chest.setLootTable(ResourceKey.create(Registries.LOOT_TABLE, Identifier.parse("minecraft:chests/nether_bridge")), random.nextLong());
			}
		}
	}

	private static void bastionRubble(ServerLevel level, BlockPos at, RandomSource random) {
		for (int i = 0; i < 18; i++) {
			BlockPos p = at.offset(random.nextInt(5) - 2, random.nextInt(3), random.nextInt(5) - 2);
			int r = random.nextInt(10);
			set(level, p, r == 0 ? Blocks.GILDED_BLACKSTONE.defaultBlockState() : r < 3 ? Blocks.POLISHED_BLACKSTONE_BRICKS.defaultBlockState()
				: r == 3 ? Blocks.GOLD_BLOCK.defaultBlockState() : Blocks.BLACKSTONE.defaultBlockState());
		}
	}

	/** A broken obsidian frame, the way Minecraft's ruined portals look: obsidian for your own portal. */
	private static void ruinedPortal(ServerLevel level, BlockPos at, RandomSource random) {
		for (int dx = 0; dx < 4; dx++) {
			for (int dy = 0; dy < 5; dy++) {
				boolean edge = dx == 0 || dx == 3 || dy == 0 || dy == 4;
				if (edge && random.nextInt(4) != 0) {
					set(level, at.offset(dx, dy, 0), random.nextInt(5) == 0 ? Blocks.CRYING_OBSIDIAN.defaultBlockState() : Blocks.OBSIDIAN.defaultBlockState());
				}
			}
		}
		set(level, at.offset(1, 0, 2), Blocks.CHEST.defaultBlockState());
		if (level.getBlockEntity(at.offset(1, 0, 2)) instanceof RandomizableContainer chest) {
			chest.setLootTable(ResourceKey.create(Registries.LOOT_TABLE, Identifier.parse("minecraft:chests/ruined_portal")), random.nextLong());
		}
	}

	private static void basaltColumns(ServerLevel level, BlockPos at, RandomSource random) {
		for (int i = 0; i < 5; i++) {
			BlockPos p = at.offset(random.nextInt(5) - 2, 0, random.nextInt(5) - 2);
			int h = 2 + random.nextInt(5);
			for (int y = 0; y < h; y++) {
				set(level, p.above(y), Blocks.BASALT.defaultBlockState());
			}
		}
	}

	private static void soulPatch(ServerLevel level, BlockPos at, RandomSource random) {
		for (int dx = -2; dx <= 2; dx++) {
			for (int dz = -2; dz <= 2; dz++) {
				if (dx * dx + dz * dz <= 5) {
					set(level, at.offset(dx, 0, dz), random.nextBoolean() ? Blocks.SOUL_SAND.defaultBlockState() : Blocks.SOUL_SOIL.defaultBlockState());
				}
			}
		}
		set(level, at.above(), Blocks.SOUL_LANTERN.defaultBlockState());
	}

	// ---- the End -------------------------------------------------------------------------------

	/** Obsidian pillars with end crystals round the island, some caged, and the bedrock podium. */
	private static int endArena(CommandContext<CommandSourceStack> ctx) {
		ServerLevel level = ctx.getSource().getLevel();
		BlockPos c = pos(ctx);
		int pillars = IntegerArgumentType.getInteger(ctx, "pillars");
		double radius = DoubleArgumentType.getDouble(ctx, "radius");
		int caged = IntegerArgumentType.getInteger(ctx, "caged");
		RandomSource random = level.getRandom();
		for (int i = 0; i < pillars; i++) {
			double a = Math.PI * 2.0 * i / pillars;
			BlockPos base = c.offset((int) Math.round(Math.cos(a) * radius), -8, (int) Math.round(Math.sin(a) * radius));
			int r = 2 + random.nextInt(2);
			int h = 14 + random.nextInt(4) * 3 + i % 3 * 2;
			for (int y = 0; y < h; y++) {
				for (int dx = -r; dx <= r; dx++) {
					for (int dz = -r; dz <= r; dz++) {
						if (dx * dx + dz * dz <= r * r + 1) {
							set(level, base.offset(dx, y, dz), Blocks.OBSIDIAN.defaultBlockState());
						}
					}
				}
			}
			BlockPos top = base.above(h);
			set(level, top, Blocks.BEDROCK.defaultBlockState());
			if (i < caged) {
				for (int dx = -2; dx <= 2; dx++) {
					for (int dz = -2; dz <= 2; dz++) {
						for (int dy = 0; dy <= 3; dy++) {
							if (Math.abs(dx) == 2 || Math.abs(dz) == 2 || dy == 3) {
								set(level, top.offset(dx, dy, dz), Blocks.IRON_BARS.defaultBlockState());
							}
						}
					}
				}
			}
			EndCrystal crystal = new EndCrystal(level, top.getX() + 0.5, top.getY() + 1, top.getZ() + 0.5);
			crystal.setShowBottom(true);
			crystal.addTag(TAG);
			crystal.addTag(SESSION_TAG);
			level.addFreshEntity(crystal);
		}
		// The exit podium the dragon perches on: a bedrock ring round a pillar.
		for (int dx = -3; dx <= 3; dx++) {
			for (int dz = -3; dz <= 3; dz++) {
				int d = dx * dx + dz * dz;
				if (d <= 12) {
					set(level, c.offset(dx, -1, dz), Blocks.BEDROCK.defaultBlockState());
				}
				if (d >= 7 && d <= 12) {
					set(level, c.offset(dx, 0, dz), Blocks.BEDROCK.defaultBlockState());
				}
			}
		}
		for (int y = 0; y < 4; y++) {
			set(level, c.above(y), Blocks.BEDROCK.defaultBlockState());
		}
		for (Direction d : Direction.Plane.HORIZONTAL) {
			set(level, c.above(2).relative(d), Blocks.WALL_TORCH.defaultBlockState().setValue(net.minecraft.world.level.block.WallTorchBlock.FACING, d));
		}
		SkyCraft.LOG.info("RiskOfCraft: End arena at {}: {} pillars at {} blocks, {} caged", c, pillars, radius, caged);
		return 1;
	}
}
