package dev.skycraft.mixin;

import dev.skycraft.link.StageBuilder;
import net.minecraft.world.entity.boss.enderdragon.EnderDragon;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.Shadow;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfo;

/**
 * RiskOfCraft: the Ender Dragon's death tells Risk of Rain 2 the run is won: its first death tick
 * (a fight), or /kill, which removes a dragon at once without dying. Stage cleanup removes dragons
 * with riskofcraft clear_dragons instead, which is no win.
 */
@Mixin(EnderDragon.class)
public abstract class EnderDragonMixin {
	@Shadow
	public int dragonDeathTime;

	@Inject(method = "tickDeath", at = @At("HEAD"))
	private void riskofcraft$died(CallbackInfo ci) {
		if (this.dragonDeathTime == 0) {
			StageBuilder.dragonDied((EnderDragon) (Object) this);
		}
	}

	@Inject(method = "kill", at = @At("HEAD"))
	private void riskofcraft$killed(net.minecraft.server.level.ServerLevel level, CallbackInfo ci) {
		StageBuilder.dragonDied((EnderDragon) (Object) this);
	}
}
