package dev.skycraft.mixin;

import dev.skycraft.link.StageBuilder;
import net.minecraft.core.BlockPos;
import net.minecraft.server.level.ServerPlayer;
import net.minecraft.world.entity.Entity;
import net.minecraft.world.level.block.Portal;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * RiskOfCraft: a player standing in a Nether or End portal doesn't change Minecraft dimension; Risk
 * of Rain 2 takes the whole run to its custom stage instead (StageBuilder.portal -> PortalEntered).
 */
@Mixin(Entity.class)
public abstract class EntityPortalMixin {
	@Inject(method = "setAsInsidePortal", at = @At("HEAD"), cancellable = true)
	private void riskofcraft$portal(Portal portal, BlockPos pos, CallbackInfo ci) {
		if ((Object) this instanceof ServerPlayer player && StageBuilder.portal(player, portal)) {
			ci.cancel();
		}
	}
}
