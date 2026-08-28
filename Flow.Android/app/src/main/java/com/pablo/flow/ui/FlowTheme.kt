package com.pablo.flow.ui

import android.os.Build
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.dynamicDarkColorScheme
import androidx.compose.material3.dynamicLightColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext

private val FlowDarkColors = darkColorScheme(
    primary = Color(0xFF5EEAD4), onPrimary = Color(0xFF00201D),
    primaryContainer = Color(0xFF0F4D47), onPrimaryContainer = Color(0xFF9FFEF0),
    secondary = Color(0xFFFBBF74), onSecondary = Color(0xFF2A1700),
    background = Color(0xFF0A1114), onBackground = Color(0xFFE3F2EF),
    surface = Color(0xFF0F191C), onSurface = Color(0xFFE3F2EF),
    surfaceVariant = Color(0xFF253537), onSurfaceVariant = Color(0xFFB8CCCA),
    outline = Color(0xFF829997), error = Color(0xFFFFB4AB)
)

private val FlowLightColors = lightColorScheme(
    primary = Color(0xFF006A60), onPrimary = Color.White,
    primaryContainer = Color(0xFF9FFEF0), onPrimaryContainer = Color(0xFF00201D),
    secondary = Color(0xFF8B5000), onSecondary = Color.White,
    background = Color(0xFFF5FBF9), onBackground = Color(0xFF161D1C),
    surface = Color.White, onSurface = Color(0xFF161D1C),
    surfaceVariant = Color(0xFFDCE8E5), onSurfaceVariant = Color(0xFF3F4947),
    outline = Color(0xFF6F7977), error = Color(0xFFBA1A1A)
)

@Composable
fun FlowTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    dynamicColor: Boolean = true,
    content: @Composable () -> Unit
) {
    val context = LocalContext.current
    val colors = when {
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S && darkTheme -> dynamicDarkColorScheme(context)
        dynamicColor && Build.VERSION.SDK_INT >= Build.VERSION_CODES.S -> dynamicLightColorScheme(context)
        darkTheme -> FlowDarkColors
        else -> FlowLightColors
    }
    MaterialTheme(colorScheme = colors, content = content)
}
