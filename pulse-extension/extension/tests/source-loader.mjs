// Resolve production .js import specifiers to their TypeScript source without compiling files.
import { registerHooks } from 'node:module';
registerHooks({
  resolve(specifier, context, nextResolve) {
    if (context.parentURL?.replaceAll('\\', '/').includes('/extension/src/') && specifier.startsWith('./') && specifier.endsWith('.js')) {
      return nextResolve(new URL(specifier.slice(0, -3) + '.ts', context.parentURL).href, context);
    }
    return nextResolve(specifier, context);
  },
});
