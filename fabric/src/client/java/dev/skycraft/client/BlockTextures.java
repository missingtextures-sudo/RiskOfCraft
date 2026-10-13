package dev.skycraft.client;

import com.mojang.blaze3d.platform.NativeImage;
import dev.skycraft.SkyCraft;
import dev.skycraft.link.Proto;
import dev.skycraft.link.SkyLink;
import dev.skycraft.link.StageBuilder;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import net.minecraft.client.Minecraft;
import net.minecraft.resources.Identifier;

/**
 * RiskOfCraft: block textures for Risk of Rain 2's custom stages (The Nether's netherrack, The End's
 * end stone), read from the player's own Minecraft and sent as render-ring textures with the id
 * the host asked for. Animated textures send their first frame.
 */
public final class BlockTextures {
	private BlockTextures() {
	}

	public static void init() {
		StageBuilder.textureSender = (id, name) -> {
			Minecraft.getInstance().execute(() -> send(id, name));
			return true;
		};
	}

	private static void send(int id, String name) {
		Identifier sprite = Identifier.parse(name);
		Identifier file = Identifier.fromNamespaceAndPath(sprite.getNamespace(), "textures/" + sprite.getPath() + ".png");
		try (InputStream in = Minecraft.getInstance().getResourceManager().open(file); NativeImage image = NativeImage.read(in)) {
			int w = image.getWidth(), h = Math.min(image.getHeight(), w); // first frame of a strip
			ByteBuffer pixels = ByteBuffer.allocateDirect(w * h * 4).order(ByteOrder.LITTLE_ENDIAN);
			for (int y = 0; y < h; y++) {
				for (int x = 0; x < w; x++) {
					int argb = image.getPixel(x, y);
					pixels.put((byte) (argb >> 16)).put((byte) (argb >> 8)).put((byte) argb).put((byte) (argb >>> 24));
				}
			}
			pixels.flip();
			ByteBuffer header = ByteBuffer.allocate(16).order(ByteOrder.LITTLE_ENDIAN).putInt(id).putInt(w).putInt(h).putInt(0).flip();
			boolean ok = SkyLink.writeRender(Proto.REN_TEXTURE, header, pixels);
			SkyCraft.LOG.info("RiskOfCraft: sent block texture {} ({}x{}) as id {}: {}", file, w, h, id, ok ? "ok" : "ring full");
		} catch (Exception e) {
			SkyCraft.LOG.warn("RiskOfCraft: block texture {} not sent: {}", file, e.toString());
		}
	}
}
