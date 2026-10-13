package dev.skycraft.mixin;

import dev.skycraft.world.PlacedBlocks;
import net.minecraft.world.item.BlockItem;
import net.minecraft.world.item.context.BlockPlaceContext;
import net.minecraft.world.level.block.state.BlockState;
import org.spongepowered.asm.mixin.Mixin;
import org.spongepowered.asm.mixin.injection.At;
import org.spongepowered.asm.mixin.injection.Inject;
import org.spongepowered.asm.mixin.injection.callback.CallbackInfoReturnable;

/** RiskOfCraft: remembers every block a player places, so a new Risk of Rain stage can clear them. */
@Mixin(BlockItem.class)
public abstract class BlockItemPlaceMixin {
	@Inject(method = "placeBlock", at = @At("RETURN"))
	private void riskofcraft$remember(BlockPlaceContext context, BlockState state, CallbackInfoReturnable<Boolean> cir) {
		if (cir.getReturnValueZ() && !context.getLevel().isClientSide()) {
			PlacedBlocks.remember(context.getClickedPos());
		}
	}
}
