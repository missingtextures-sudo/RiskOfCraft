package dev.skycraft.client.mixin;

import dev.skycraft.link.StageBuilder;
import net.minecraft.client.gui.screens.WinScreen;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/** RiskOfCraft: the End Poem and credits are over (watched to the end or skipped): Risk of Rain 2 ends the run. */
@Mixin(WinScreen.class)
public abstract class WinScreenMixin {
	@Inject(method = "respawn", at = @At("HEAD"))
	private void riskofcraft$creditsDone(CallbackInfo ci) {
		StageBuilder.creditsDone();
	}
}
