package dev.skycraft.mixin;

import dev.skycraft.world.SkyCollision;
import net.minecraft.core.BlockPos;
import net.minecraft.world.level.BlockGetter;
import net.minecraft.world.level.pathfinder.PathType;
import net.minecraft.world.level.pathfinder.WalkNodeEvaluator;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/**
 * RiskOfCraft: Minecraft's mobs walk on Risk of Rain 2's ground. It is triangles, not blocks, so
 * the walk planner saw only air; a cell mostly inside that geometry now counts as blocked (solid),
 * which makes the air above it a place to stand, and piglins and wither skeletons can find Steve.
 */
@Mixin(WalkNodeEvaluator.class)
public abstract class WalkNodeEvaluatorMixin {
	@Inject(method = "getPathTypeFromState", at = @At("HEAD"), cancellable = true)
	private static void riskofcraft$skyGroundIsSolid(BlockGetter level, BlockPos pos, CallbackInfoReturnable<PathType> cir) {
		if (level.getBlockState(pos).isAir() && SkyCollision.solidFraction(pos) >= 0.5F) {
			cir.setReturnValue(PathType.BLOCKED);
		}
	}
}
