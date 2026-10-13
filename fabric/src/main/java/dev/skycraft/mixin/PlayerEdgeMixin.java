package dev.skycraft.mixin;

import dev.skycraft.link.SkyLink;
import dev.skycraft.world.SkyCollision;
import dev.skycraft.world.SkyTri;
import dev.skycraft.world.TriCollider;
import java.util.ArrayList;
import java.util.List;
import net.minecraft.world.entity.player.Player;
import net.minecraft.world.phys.AABB;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * Crouching stops at edges of the host's ground too. Minecraft looks for block collision under the
 * player to decide where an edge is; the host ground the player walks on isn't blocks (the player
 * collides with its exact triangles), so to Minecraft every direction looked like a drop. Here the
 * triangles count as ground for that test, and Minecraft's own edge logic does the rest.
 */
@Mixin(Player.class)
public abstract class PlayerEdgeMixin {
	@Inject(method = "canFallAtLeast", at = @At("RETURN"), cancellable = true)
	private void skycraft$hostGroundHoldsToo(double deltaX, double deltaZ, double minHeight, CallbackInfoReturnable<Boolean> cir) {
		Player self = (Player) (Object) this;
		if (!cir.getReturnValue() || !SkyLink.active() || !SkyCollision.usesSmoothCollider(self)) {
			return;
		}
		AABB box = self.getBoundingBox();
		double x = (box.minX + box.maxX) * 0.5 + deltaX;
		double z = (box.minZ + box.maxZ) * 0.5 + deltaZ;
		double feet = box.minY;
		List<SkyTri> tris = new ArrayList<>();
		SkyCollision.trianglesNear(new AABB(x - 1.0, feet - minHeight - 1.0, z - 1.0, x + 1.0, feet + 1.0, z + 1.0), tris);
		if (tris.isEmpty()) {
			return;
		}
		// The highest surface under the footprint, up to a hair above the feet: close enough below is ground.
		double ground = TriCollider.groundAt(tris, x, feet, z, 0.05);
		if (!Double.isNaN(ground) && ground >= feet - minHeight - 1.0E-4) {
			cir.setReturnValue(false);
		}
	}
}
