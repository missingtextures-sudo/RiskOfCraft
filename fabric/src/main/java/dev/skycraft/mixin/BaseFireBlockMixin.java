package dev.skycraft.mixin;

import net.minecraft.world.level.Level;
import net.minecraft.world.level.block.BaseFireBlock;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** RiskOfCraft: Nether portals light in the mirror world too (vanilla allows only the Overworld and the Nether). */
@Mixin(BaseFireBlock.class)
public abstract class BaseFireBlockMixin {
	@Inject(method = "inPortalDimension", at = @At("HEAD"), cancellable = true)
	private static void riskofcraft$anyDimension(Level level, CallbackInfoReturnable<Boolean> cir) {
		cir.setReturnValue(true);
	}
}
