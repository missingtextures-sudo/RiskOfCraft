package dev.skycraft.link;

import static java.lang.foreign.ValueLayout.JAVA_INT;
import static java.lang.foreign.ValueLayout.JAVA_LONG;

import com.mojang.brigadier.arguments.FloatArgumentType;
import dev.skycraft.SkyCraft;
import java.lang.foreign.MemorySegment;
import java.lang.invoke.VarHandle;
import java.nio.charset.StandardCharsets;
import java.util.List;
import net.fabricmc.fabric.api.command.v2.CommandRegistrationCallback;
import net.fabricmc.fabric.api.event.lifecycle.v1.ServerTickEvents;
import net.minecraft.commands.CommandSourceStack;
import net.minecraft.commands.Commands;
import net.minecraft.network.chat.Component;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.server.permissions.LevelBasedPermissionSet;

/**
 * RiskOfCraft: Risk of Rain 2 asks Minecraft to do things through the command ring (protocol 12):
 * the starter kit, loot-table rolls from chests and kills, XP, healing and the attribute scaling.
 * Each entry is an ordinary Minecraft command, run on the server thread as the player with owner
 * rights, so it goes through Minecraft's own rules (loot tables, attributes, inventory).
 *
 * Also registers /riskofcraft for what vanilla commands can't do: heal by an exact amount, reset
 * the player for a new run, and clear the blocks placed on the last stage.
 */
public final class HostCommands {
	private static final VarHandle LONG = JAVA_LONG.varHandle();

	private HostCommands() {
	}

	public static void init() {
		ServerTickEvents.END_SERVER_TICK.register(HostCommands::drain);
		CommandRegistrationCallback.EVENT.register((dispatcher, registry, environment) -> dispatcher.register(
			Commands.literal("riskofcraft")
				.then(Commands.literal("heal").then(Commands.argument("amount", FloatArgumentType.floatArg(0.0F)).executes(ctx -> {
					ServerPlayer player = ctx.getSource().getPlayerOrException();
					float amount = FloatArgumentType.getFloat(ctx, "amount");
					player.heal(amount);
					return 1;
				})))
				.then(Commands.literal("clear_placed").executes(ctx -> {
					dev.skycraft.world.PlacedBlocks.clear(ctx.getSource().getPlayerOrException().level());
					return 1;
				}))
				.then(Commands.literal("reset_run").executes(ctx -> {
					resetRun(ctx.getSource().getPlayerOrException());
					return 1;
				}))
		));
	}

	/** A new Risk of Rain run: empty inventory, full health and food, no XP, no effects. */
	private static void resetRun(ServerPlayer player) {
		player.getInventory().clearContent();
		player.setGameMode(net.minecraft.world.level.GameType.SURVIVAL); // a run is always Survival
		player.removeAllEffects();
		player.setExperienceLevels(0);
		player.setExperiencePoints(0);
		player.setHealth(player.getMaxHealth());
		player.getFoodData().setFoodLevel(20);
		player.getFoodData().setSaturation(5.0F);
		player.fallDistance = 0.0;
		player.clearFire();
		SkyCraft.LOG.info("RiskOfCraft: run reset for {}", player.getName().getString());
	}

	/** Runs every queued host command (server thread). Commands wait while no player is in the world. */
	private static void drain(MinecraftServer server) {
		MemorySegment s = SkyLink.segment();
		if (s == null) {
			return;
		}
		List<ServerPlayer> players = server.getPlayerList().getPlayers();
		if (players.isEmpty()) {
			return;
		}
		// The host's own player is the one on this machine: the integrated server's first player.
		ServerPlayer player = players.getFirst();
		long base = RocProto.OFF_COMMAND_RING;
		long head = (long) LONG.getAcquire(s, base + RocProto.CR_HEAD);
		long tail = s.get(JAVA_LONG, base + RocProto.CR_TAIL);
		if (head - tail > RocProto.COMMAND_RING_ENTRIES) {
			tail = head - RocProto.COMMAND_RING_ENTRIES; // the host lapped us; drop the oldest
		}
		int ran = 0;
		while (tail < head && ran < 32) {
			long e = base + RocProto.CR_DATA + (tail & (RocProto.COMMAND_RING_ENTRIES - 1)) * RocProto.COMMAND_BYTES;
			int id = s.get(JAVA_INT, e + RocProto.HC_ID);
			int length = Math.min(Math.max(s.get(JAVA_INT, e + RocProto.HC_LENGTH), 0), RocProto.HC_TEXT_BYTES);
			byte[] text = new byte[length];
			MemorySegment.copy(s, java.lang.foreign.ValueLayout.JAVA_BYTE, e + RocProto.HC_TEXT, text, 0, length);
			tail++;
			ran++;
			run(server, player, id, new String(text, StandardCharsets.UTF_8));
		}
		LONG.setRelease(s, base + RocProto.CR_TAIL, tail);
	}

	private static void run(MinecraftServer server, ServerPlayer player, int id, String command) {
		CommandSourceStack source = player.createCommandSourceStack()
			.withPermission(LevelBasedPermissionSet.OWNER)
			.withSuppressedOutput();
		try {
			// Minecraft reports a command it can't parse to the (silenced) source: say so here.
			var parsed = server.getCommands().getDispatcher().parse(command, source);
			if (parsed.getReader().canRead() || parsed.getContext().getLastChild().getCommand() == null) {
				var problem = parsed.getExceptions().values().stream().findFirst();
				SkyCraft.LOG.warn("RiskOfCraft: host command {} doesn't parse at column {}: {}{}", id, parsed.getReader().getCursor(), command,
					problem.map(x -> " (" + x.getMessage() + ")").orElse(""));
			}
			server.getCommands().performPrefixedCommand(source, command);
			SkyCraft.LOG.info("RiskOfCraft: host command {}: {}", id, command);
		} catch (RuntimeException ex) {
			SkyCraft.LOG.warn("RiskOfCraft: host command {} failed: {} ({})", id, command, ex.toString());
			player.sendSystemMessage(Component.literal("RiskOfCraft: a Risk of Rain command failed: " + command));
		}
	}
}
