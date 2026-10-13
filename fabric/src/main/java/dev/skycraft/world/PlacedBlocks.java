package dev.skycraft.world;

import dev.skycraft.SkyCraft;
import it.unimi.dsi.fastutil.longs.LongOpenHashSet;
import java.io.DataInputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.Path;
import net.minecraft.core.BlockPos;
import net.minecraft.server.MinecraftServer;
import net.minecraft.server.level.ServerLevel;
import net.minecraft.world.level.block.Blocks;
import net.minecraft.world.level.storage.LevelResource;

/**
 * RiskOfCraft: the blocks placed since the last Risk of Rain stage started (by players, and by
 * RiskOfCraft's chests, portals, decorations and the End arena). Every stage is a new place in the
 * same mirror world, so at the next stage they are removed again ({@code /riskofcraft clear_placed},
 * sent by Risk of Rain 2 when a stage starts). The list is kept in the world folder, so blocks from
 * a session that ended (Minecraft closed mid-stage) are still cleared by the next one.
 */
public final class PlacedBlocks {
	private static final LongOpenHashSet PLACED = new LongOpenHashSet();
	private static final String FILE = "riskofcraft_placed.bin";
	private static boolean dirty;

	private PlacedBlocks() {
	}

	public static synchronized void remember(BlockPos pos) {
		if (PLACED.add(pos.asLong())) {
			dirty = true;
		}
	}

	/** Sets every remembered block back to air (neighbours update, so doors and beds go whole). */
	public static synchronized int clear(ServerLevel level) {
		int removed = 0;
		for (long packed : PLACED) {
			BlockPos pos = BlockPos.of(packed);
			if (!level.getBlockState(pos).isAir()) {
				level.setBlock(pos, Blocks.AIR.defaultBlockState(), 3);
				removed++;
			}
		}
		PLACED.clear();
		dirty = true;
		save(level.getServer());
		SkyCraft.LOG.info("RiskOfCraft: cleared {} placed blocks for the new stage", removed);
		return removed;
	}

	private static Path file(MinecraftServer server) {
		return server.getWorldPath(LevelResource.ROOT).resolve(FILE);
	}

	/** World loaded: the last session's blocks are still to clear. */
	public static synchronized void load(MinecraftServer server) {
		PLACED.clear();
		Path path = file(server);
		if (!Files.exists(path)) {
			return;
		}
		try (DataInputStream in = new DataInputStream(Files.newInputStream(path))) {
			int count = in.readInt();
			for (int i = 0; i < count; i++) {
				PLACED.add(in.readLong());
			}
			SkyCraft.LOG.info("RiskOfCraft: {} blocks from the last session to clear at the next stage", count);
		} catch (IOException ex) {
			SkyCraft.LOG.warn("RiskOfCraft: couldn't read {}", path, ex);
		}
		dirty = false;
	}

	/** Every few seconds and when the world closes: write the list if it changed. */
	public static synchronized void save(MinecraftServer server) {
		if (!dirty) {
			return;
		}
		Path path = file(server);
		try (DataOutputStream out = new DataOutputStream(Files.newOutputStream(path))) {
			out.writeInt(PLACED.size());
			for (long packed : PLACED) {
				out.writeLong(packed);
			}
			dirty = false;
		} catch (IOException ex) {
			SkyCraft.LOG.warn("RiskOfCraft: couldn't write {}", path, ex);
		}
	}
}
